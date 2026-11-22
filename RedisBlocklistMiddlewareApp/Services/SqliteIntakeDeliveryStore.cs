using AiScrapingDefense.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class SqliteIntakeDeliveryStore : IIntakeDeliveryStore
{
    private readonly string _connectionString;
    private readonly int _maxRecentEvents;
    private readonly object _gate = new();

    public SqliteIntakeDeliveryStore(
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
            DataSource = resolvedDatabasePath,
            Pooling = false
        }.ToString();
        _maxRecentEvents = options.Value.Audit.MaxRecentEvents;
        EnsureSchema();
    }

    public void Add(IntakeDeliveryRecord record)
    {
        // Whatever produced the record, a URL that may carry a credential is never persisted (#199).
        record = record.Redacted();
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO intake_delivery_events
                (
                    delivery_type,
                    channel,
                    ip_address,
                    reason,
                    target,
                    status,
                    detail,
                    attempted_at_utc
                )
                VALUES
                (
                    $deliveryType,
                    $channel,
                    $ipAddress,
                    $reason,
                    $target,
                    $status,
                    $detail,
                    $attemptedAtUtc
                );
                """;
            command.Parameters.AddWithValue("$deliveryType", record.DeliveryType);
            command.Parameters.AddWithValue("$channel", record.Channel);
            command.Parameters.AddWithValue("$ipAddress", record.IpAddress);
            command.Parameters.AddWithValue("$reason", record.Reason);
            command.Parameters.AddWithValue("$target", record.Target);
            command.Parameters.AddWithValue("$status", record.Status);
            command.Parameters.AddWithValue("$detail", record.Detail);
            command.Parameters.AddWithValue("$attemptedAtUtc", record.AttemptedAtUtc.UtcDateTime.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<IntakeDeliveryRecord> GetRecent(int count)
    {
        var safeCount = Math.Clamp(count, 1, _maxRecentEvents);
        var results = new List<IntakeDeliveryRecord>(safeCount);

        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    delivery_type,
                    channel,
                    ip_address,
                    reason,
                    target,
                    status,
                    detail,
                    attempted_at_utc
                FROM intake_delivery_events
                ORDER BY attempted_at_utc DESC, id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", safeCount);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new IntakeDeliveryRecord(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetString(6),
                    DateTimeOffset.Parse(reader.GetString(7))).Redacted());
            }
        }

        return results;
    }

    public IntakeDeliveryMetrics GetMetrics()
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    COUNT(*),
                    COALESCE(SUM(CASE WHEN status = 'succeeded' THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN status = 'failed' THEN 1 ELSE 0 END), 0),
                    COALESCE(SUM(CASE WHEN status = 'skipped' THEN 1 ELSE 0 END), 0),
                    MAX(attempted_at_utc)
                FROM intake_delivery_events;
                """;

            using var reader = command.ExecuteReader();
            reader.Read();

            return new IntakeDeliveryMetrics(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : DateTimeOffset.Parse(reader.GetString(4)));
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
                CREATE TABLE IF NOT EXISTS intake_delivery_events
                (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    delivery_type TEXT NOT NULL,
                    channel TEXT NOT NULL,
                    ip_address TEXT NOT NULL,
                    reason TEXT NOT NULL,
                    target TEXT NOT NULL,
                    status TEXT NOT NULL,
                    detail TEXT NOT NULL,
                    attempted_at_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_intake_delivery_events_attempted_at
                    ON intake_delivery_events (attempted_at_utc DESC, id DESC);
                """;
            command.ExecuteNonQuery();
            ScrubStoredUrls(connection);
        }
    }

    /// <summary>
    /// Rows written by earlier releases hold the full webhook URL, which can carry a credential. Rewrite them in place;
    /// the pass is idempotent, so it costs one query on later starts (#199).
    /// </summary>
    private static void ScrubStoredUrls(SqliteConnection connection)
    {
        var rows = new List<(long Id, string Target, string Detail)>();
        using (var select = connection.CreateCommand())
        {
            select.CommandText =
                "SELECT id, target, detail FROM intake_delivery_events WHERE target LIKE '%://%' OR detail LIKE '%://%';";
            using var reader = select.ExecuteReader();
            while (reader.Read())
            {
                rows.Add((reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        if (rows.Count == 0)
        {
            return;
        }

        using var transaction = connection.BeginTransaction();
        foreach (var (id, target, detail) in rows)
        {
            var safeTarget = UrlRedaction.SafeTarget(target);
            var safeDetail = UrlRedaction.RedactUrls(detail);
            if (safeTarget == target && safeDetail == detail)
            {
                continue;
            }

            using var update = connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE intake_delivery_events SET target = $target, detail = $detail WHERE id = $id;";
            update.Parameters.AddWithValue("$target", safeTarget);
            update.Parameters.AddWithValue("$detail", safeDetail);
            update.Parameters.AddWithValue("$id", id);
            update.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
