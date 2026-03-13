using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class SqliteWebhookEventInbox : IWebhookEventInbox
{
    private readonly string _connectionString;
    private readonly Channel<bool> _signalChannel = Channel.CreateUnbounded<bool>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly object _gate = new();

    public SqliteWebhookEventInbox(
        IOptions<DefenseEngineOptions> options,
        IHostEnvironment environment)
    {
        var databasePath = options.Value.Audit.DatabasePath;
        var resolvedDatabasePath = Path.IsPathRooted(databasePath)
            ? databasePath
            : Path.Combine(environment.ContentRootPath, databasePath);

        var directory = Path.GetDirectoryName(resolvedDatabasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = resolvedDatabasePath
        }.ToString();

        EnsureSchema();
        ResetLeases();
    }

    public Task<long> EnqueueAsync(IntakeWebhookEvent webhookEvent, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO webhook_intake_events
                (
                    event_type,
                    reason,
                    timestamp_utc,
                    payload_json,
                    status,
                    leased_at_utc
                )
                VALUES
                (
                    $eventType,
                    $reason,
                    $timestampUtc,
                    $payloadJson,
                    'pending',
                    NULL
                );

                SELECT last_insert_rowid();
                """;
            command.Parameters.AddWithValue("$eventType", webhookEvent.EventType);
            command.Parameters.AddWithValue("$reason", webhookEvent.Reason);
            command.Parameters.AddWithValue("$timestampUtc", webhookEvent.TimestampUtc.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$payloadJson", JsonSerializer.Serialize(webhookEvent));
            var id = (long)command.ExecuteScalar()!;
            _signalChannel.Writer.TryWrite(true);
            return Task.FromResult(id);
        }
    }

    public async IAsyncEnumerable<WebhookInboxItem> ReadAllAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var next = TryClaimNext();
            if (next is not null)
            {
                yield return next;
                continue;
            }

            await _signalChannel.Reader.ReadAsync(cancellationToken);
        }
    }

    public Task CompleteAsync(long id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                DELETE FROM webhook_intake_events
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
            return Task.CompletedTask;
        }
    }

    public Task AbandonAsync(long id, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE webhook_intake_events
                SET
                    status = 'pending',
                    leased_at_utc = NULL
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
            _signalChannel.Writer.TryWrite(true);
            return Task.CompletedTask;
        }
    }

    private WebhookInboxItem? TryClaimNext()
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            using var selectCommand = connection.CreateCommand();
            selectCommand.Transaction = transaction;
            selectCommand.CommandText =
                """
                SELECT id, payload_json
                FROM webhook_intake_events
                WHERE status = 'pending'
                ORDER BY id
                LIMIT 1;
                """;

            using var reader = selectCommand.ExecuteReader();
            if (!reader.Read())
            {
                transaction.Commit();
                return null;
            }

            var id = reader.GetInt64(0);
            var payloadJson = reader.GetString(1);
            reader.Close();

            using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText =
                """
                UPDATE webhook_intake_events
                SET
                    status = 'processing',
                    leased_at_utc = $leasedAtUtc
                WHERE id = $id;
                """;
            updateCommand.Parameters.AddWithValue("$id", id);
            updateCommand.Parameters.AddWithValue("$leasedAtUtc", DateTimeOffset.UtcNow.UtcDateTime.ToString("O"));
            updateCommand.ExecuteNonQuery();

            transaction.Commit();

            var webhookEvent = JsonSerializer.Deserialize<IntakeWebhookEvent>(payloadJson)
                ?? throw new InvalidOperationException($"Failed to deserialize webhook inbox payload {id}.");

            return new WebhookInboxItem(id, webhookEvent);
        }
    }

    private void EnsureSchema()
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS webhook_intake_events
                (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    event_type TEXT NOT NULL,
                    reason TEXT NOT NULL,
                    timestamp_utc TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    status TEXT NOT NULL,
                    leased_at_utc TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_webhook_intake_events_status_id
                    ON webhook_intake_events (status, id);
                """;
            command.ExecuteNonQuery();
        }
    }

    private void ResetLeases()
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE webhook_intake_events
                SET
                    status = 'pending',
                    leased_at_utc = NULL
                WHERE status = 'processing';
                """;
            command.ExecuteNonQuery();

            using var countCommand = connection.CreateCommand();
            countCommand.CommandText =
                """
                SELECT COUNT(*)
                FROM webhook_intake_events
                WHERE status = 'pending';
                """;
            var pendingCount = Convert.ToInt32(countCommand.ExecuteScalar());
            for (var i = 0; i < pendingCount; i++)
            {
                _signalChannel.Writer.TryWrite(true);
            }
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
