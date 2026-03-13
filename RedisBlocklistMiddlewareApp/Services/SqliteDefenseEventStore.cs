using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class SqliteDefenseEventStore : IDefenseEventStore
{
    private readonly string _connectionString;
    private readonly int _maxRecentEvents;
    private readonly object _gate = new();

    public SqliteDefenseEventStore(
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
        _maxRecentEvents = options.Value.Audit.MaxRecentEvents;

        EnsureSchema();
    }

    public void Add(DefenseDecision decision)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO defense_events
                (
                    ip_address,
                    action,
                    score,
                    frequency,
                    path,
                    signals_json,
                    summary,
                    observed_at_utc,
                    decided_at_utc
                )
                VALUES
                (
                    $ipAddress,
                    $action,
                    $score,
                    $frequency,
                    $path,
                    $signalsJson,
                    $summary,
                    $observedAtUtc,
                    $decidedAtUtc
                );
                """;

            command.Parameters.AddWithValue("$ipAddress", decision.IpAddress);
            command.Parameters.AddWithValue("$action", decision.Action);
            command.Parameters.AddWithValue("$score", decision.Score);
            command.Parameters.AddWithValue("$frequency", decision.Frequency);
            command.Parameters.AddWithValue("$path", decision.Path);
            command.Parameters.AddWithValue("$signalsJson", JsonSerializer.Serialize(decision.Signals));
            command.Parameters.AddWithValue("$summary", decision.Summary);
            command.Parameters.AddWithValue("$observedAtUtc", decision.ObservedAtUtc.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$decidedAtUtc", decision.DecidedAtUtc.UtcDateTime.ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<DefenseDecision> GetRecent(int count)
    {
        var safeCount = Math.Clamp(count, 1, _maxRecentEvents);
        var results = new List<DefenseDecision>(safeCount);

        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    ip_address,
                    action,
                    score,
                    frequency,
                    path,
                    signals_json,
                    summary,
                    observed_at_utc,
                    decided_at_utc
                FROM defense_events
                ORDER BY decided_at_utc DESC, id DESC
                LIMIT $limit;
                """;
            command.Parameters.AddWithValue("$limit", safeCount);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new DefenseDecision(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetInt32(2),
                    reader.GetInt64(3),
                    reader.GetString(4),
                    JsonSerializer.Deserialize<string[]>(reader.GetString(5)) ?? [],
                    reader.GetString(6),
                    DateTimeOffset.Parse(reader.GetString(7)),
                    DateTimeOffset.Parse(reader.GetString(8))));
            }
        }

        return results;
    }

    public DefenseEventMetrics GetMetrics()
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    COUNT(*) AS total_decisions,
                    COALESCE(SUM(CASE WHEN action = 'blocked' THEN 1 ELSE 0 END), 0) AS blocked_count,
                    COALESCE(SUM(CASE WHEN action = 'observed' THEN 1 ELSE 0 END), 0) AS observed_count,
                    MAX(decided_at_utc) AS latest_decision_at_utc
                FROM defense_events;
                """;

            using var reader = command.ExecuteReader();
            reader.Read();

            var latestDecisionAtUtc = reader.IsDBNull(3)
                ? (DateTimeOffset?)null
                : DateTimeOffset.Parse(reader.GetString(3));

            return new DefenseEventMetrics(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                latestDecisionAtUtc);
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
                CREATE TABLE IF NOT EXISTS defense_events
                (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ip_address TEXT NOT NULL,
                    action TEXT NOT NULL,
                    score INTEGER NOT NULL,
                    frequency INTEGER NOT NULL,
                    path TEXT NOT NULL,
                    signals_json TEXT NOT NULL,
                    summary TEXT NOT NULL,
                    observed_at_utc TEXT NOT NULL,
                    decided_at_utc TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_defense_events_decided_at
                    ON defense_events (decided_at_utc DESC, id DESC);
                """;
            command.ExecuteNonQuery();
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }
}
