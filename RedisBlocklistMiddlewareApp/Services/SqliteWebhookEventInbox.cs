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
    // A single coalesced wake-up: the reader always re-checks the database, so extra signals add nothing.
    private readonly Channel<bool> _signalChannel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite
    });
    private readonly object _gate = new();
    private readonly TimeSpan _leaseTimeout;
    private readonly int _maxAttempts;
    private readonly TimeSpan _retryBase;
    private readonly TimeSpan _retryMax;

    public SqliteWebhookEventInbox(
        IOptions<DefenseEngineOptions> options,
        IHostEnvironment environment)
    {
        var databasePath = options.Value.Audit.DatabasePath;
        _leaseTimeout = TimeSpan.FromMinutes(
            Math.Max(1, options.Value.Queue.WebhookLeaseTimeoutMinutes));
        _maxAttempts = Math.Max(1, options.Value.Queue.WebhookMaxAttempts);
        _retryBase = TimeSpan.FromMilliseconds(Math.Max(1, options.Value.Queue.WebhookRetryBaseMilliseconds));
        _retryMax = TimeSpan.FromMilliseconds(
            Math.Max(_retryBase.TotalMilliseconds, options.Value.Queue.WebhookRetryMaxMilliseconds));
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
            DataSource = resolvedDatabasePath,
            Pooling = false
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

            // Nothing is ready. Wake on a new item, or when the earliest backed-off retry is due.
            var retryDelay = GetDelayUntilNextRetry();
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (retryDelay is not null)
            {
                wait.CancelAfter(retryDelay.Value);
            }

            try
            {
                await _signalChannel.Reader.ReadAsync(wait.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The retry delay elapsed.
            }
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
            using var select = connection.CreateCommand();
            select.CommandText = "SELECT attempts FROM webhook_intake_events WHERE id = $id;";
            select.Parameters.AddWithValue("$id", id);
            var current = select.ExecuteScalar();
            if (current is null)
            {
                return Task.CompletedTask;
            }

            var attempts = Convert.ToInt32(current) + 1;
            var dead = attempts >= _maxAttempts;
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE webhook_intake_events
                SET
                    status = $status,
                    leased_at_utc = NULL,
                    attempts = $attempts,
                    not_before_utc = $notBefore
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$status", dead ? DeadStatus : "pending");
            command.Parameters.AddWithValue("$attempts", attempts);
            command.Parameters.AddWithValue(
                "$notBefore",
                dead ? DBNull.Value : (object)Iso(DateTimeOffset.UtcNow + RetryDelay(attempts)));
            command.ExecuteNonQuery();
            _signalChannel.Writer.TryWrite(true);
            return Task.CompletedTask;
        }
    }

    // Exponential backoff: base, 2x base, 4x base ... capped at the configured maximum.
    private TimeSpan RetryDelay(int attempts)
    {
        var factor = Math.Pow(2, Math.Min(attempts - 1, 30));
        return TimeSpan.FromMilliseconds(Math.Min(_retryMax.TotalMilliseconds, _retryBase.TotalMilliseconds * factor));
    }

    private static string Iso(DateTimeOffset value) => value.UtcDateTime.ToString("O");

    private TimeSpan? GetDelayUntilNextRetry()
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT MIN(not_before_utc)
                FROM webhook_intake_events
                WHERE status = 'pending' AND not_before_utc IS NOT NULL;
                """;
            if (command.ExecuteScalar() is not string due
                || !DateTimeOffset.TryParse(due, null, System.Globalization.DateTimeStyles.AssumeUniversal, out var dueAt))
            {
                return null;
            }

            var delay = dueAt - DateTimeOffset.UtcNow;
            return delay < TimeSpan.FromMilliseconds(10) ? TimeSpan.FromMilliseconds(10) : delay;
        }
    }

    private const string DeadStatus = "dead";

    private WebhookInboxItem? TryClaimNext()
    {
        while (true)
        {
            var claimed = ClaimNextRow();
            if (claimed is null)
            {
                return null;
            }

            try
            {
                var webhookEvent = JsonSerializer.Deserialize<IntakeWebhookEvent>(claimed.Value.PayloadJson);
                if (webhookEvent?.Details is not null)
                {
                    return new WebhookInboxItem(claimed.Value.Id, webhookEvent);
                }
            }
            catch (JsonException)
            {
                // Permanent failure: fall through to dead-letter.
            }

            MarkDead(claimed.Value.Id);
        }
    }

    private (long Id, string PayloadJson)? ClaimNextRow()
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
                  AND (not_before_utc IS NULL OR not_before_utc <= $now)
                ORDER BY id
                LIMIT 1;
                """;
            selectCommand.Parameters.AddWithValue("$now", Iso(DateTimeOffset.UtcNow));

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
            updateCommand.Parameters.AddWithValue("$leasedAtUtc", Iso(DateTimeOffset.UtcNow));
            updateCommand.ExecuteNonQuery();

            transaction.Commit();
            return (id, payloadJson);
        }
    }

    private void MarkDead(long id)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE webhook_intake_events
                SET status = 'dead', leased_at_utc = NULL, not_before_utc = NULL
                WHERE id = $id;
                """;
            command.Parameters.AddWithValue("$id", id);
            command.ExecuteNonQuery();
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
                    leased_at_utc TEXT NULL,
                    attempts INTEGER NOT NULL DEFAULT 0,
                    not_before_utc TEXT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_webhook_intake_events_status_id
                    ON webhook_intake_events (status, id);
                """;
            command.ExecuteNonQuery();

            // Databases created before retry tracking existed lack these columns.
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var info = connection.CreateCommand())
            {
                info.CommandText = "PRAGMA table_info(webhook_intake_events);";
                using var reader = info.ExecuteReader();
                while (reader.Read())
                {
                    existing.Add(reader.GetString(1));
                }
            }

            foreach (var (column, definition) in new[]
            {
                ("attempts", "INTEGER NOT NULL DEFAULT 0"),
                ("not_before_utc", "TEXT NULL")
            })
            {
                if (existing.Contains(column))
                {
                    continue;
                }

                using var alter = connection.CreateCommand();
                alter.CommandText = $"ALTER TABLE webhook_intake_events ADD COLUMN {column} {definition};";
                alter.ExecuteNonQuery();
            }
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
                WHERE status = 'processing'
                  AND (leased_at_utc IS NULL OR leased_at_utc < $leaseCutoffUtc);
                """;
            command.Parameters.AddWithValue(
                "$leaseCutoffUtc",
                (DateTimeOffset.UtcNow - _leaseTimeout).UtcDateTime.ToString("O"));
            command.ExecuteNonQuery();

            // One coalesced signal is enough: the reader drains the table.
            _signalChannel.Writer.TryWrite(true);
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
