using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

/// <summary>
/// What the audit stores consult before persisting optional detail. When the audit database nears its configured size limit
/// (#197) the stores stop saving the score breakdown (the bulkiest, least essential column) so the decisions themselves can
/// keep being recorded.
/// </summary>
public sealed class AuditStorageState
{
    private long _bytes;
    private volatile bool _shedDetail;

    /// <summary>The fraction of <c>Audit:MaxDatabaseBytes</c> at which detail is shed.</summary>
    public const double ShedAtFraction = 0.9;

    public bool ShedDetail => _shedDetail;

    public long Bytes => Interlocked.Read(ref _bytes);

    public void Update(long bytes, long limitBytes)
    {
        Interlocked.Exchange(ref _bytes, bytes);
        _shedDetail = limitBytes > 0 && bytes >= limitBytes * ShedAtFraction;
    }
}

/// <summary>Measures how large the audit database is, whichever provider holds it.</summary>
public static class AuditStorageSize
{
    public static long Measure(AuditOptions audit, string sqlitePath)
    {
        if (string.Equals(audit.Provider, AuditStorageProviders.Postgres, StringComparison.OrdinalIgnoreCase))
        {
            using var connection = new NpgsqlConnection(audit.ConnectionString);
            return Scalar(connection, "SELECT pg_database_size(current_database());");
        }

        if (string.Equals(audit.Provider, AuditStorageProviders.SqlServer, StringComparison.OrdinalIgnoreCase))
        {
            using var connection = new SqlConnection(audit.ConnectionString);
            return Scalar(connection, "SELECT COALESCE(SUM(CAST(size AS BIGINT)), 0) * 8192 FROM sys.database_files WHERE type = 0;");
        }

        // SQLite: prefer live bytes (page_count - freelist_count) * page_size so freed pages after pruning
        // count as reclaimed even before VACUUM; fall back to file sizes when the file is absent or not a SQLite db.
        if (File.Exists(sqlitePath) && TryMeasureSqliteLiveBytes(sqlitePath, out var liveBytes))
        {
            return liveBytes;
        }

        return new[] { string.Empty, "-wal", "-shm", "-journal" }
            .Select(suffix => new FileInfo(sqlitePath + suffix))
            .Where(file => file.Exists)
            .Sum(file => file.Length);
    }

    private static bool TryMeasureSqliteLiveBytes(string sqlitePath, out long liveBytes)
    {
        liveBytes = 0;
        if (!HasSqliteHeader(sqlitePath))
        {
            return false;
        }

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = sqlitePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            var pageCount = Pragma(connection, "PRAGMA page_count;");
            if (pageCount <= 0)
            {
                return false;
            }

            var freelistCount = Pragma(connection, "PRAGMA freelist_count;");
            var pageSize = Pragma(connection, "PRAGMA page_size;");
            liveBytes = Math.Max(0L, pageCount - freelistCount) * pageSize;
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static bool HasSqliteHeader(string sqlitePath)
    {
        Span<byte> header = stackalloc byte[16];
        using var stream = new FileStream(sqlitePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Read(header) < 16)
        {
            return false;
        }

        return header.SequenceEqual("SQLite format 3\0"u8);
    }

    private static long Pragma(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
    }

    private static long Scalar(DbConnection connection, string sql)
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
    }
}

/// <summary>Publishes the audit database size and its limit as metrics, sheds optional detail near the limit, and prunes oldest rows at the limit.</summary>
public sealed class AuditStorageMonitorService : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan PruneCooldown = TimeSpan.FromMinutes(5);
    internal const double PruneFraction = 0.10;
    internal const int MinimumPruneRows = 1000;

    private readonly IOptions<DefenseEngineOptions> _options;
    private readonly IHostEnvironment _environment;
    private readonly AuditStorageState _state;
    private readonly DefenseTelemetry _telemetry;
    private readonly ILogger<AuditStorageMonitorService> _logger;
    private readonly IDefenseEventStore? _eventStore;
    private readonly TimeProvider _timeProvider;
    private DateTimeOffset? _lastPruneUtc;
    private bool _warned;

    public AuditStorageMonitorService(
        IOptions<DefenseEngineOptions> options,
        IHostEnvironment environment,
        AuditStorageState state,
        DefenseTelemetry telemetry,
        ILogger<AuditStorageMonitorService> logger,
        IDefenseEventStore? eventStore = null,
        TimeProvider? timeProvider = null)
    {
        _options = options;
        _environment = environment;
        _state = state;
        _telemetry = telemetry;
        _logger = logger;
        _eventStore = eventStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public void Sample()
    {
        var audit = _options.Value.Audit;
        var path = Path.IsPathRooted(audit.DatabasePath) ? audit.DatabasePath : Path.Join(_environment.ContentRootPath, audit.DatabasePath);
        var bytes = AuditStorageSize.Measure(audit, path);

        if (audit.MaxDatabaseBytes > 0 && bytes >= audit.MaxDatabaseBytes && _eventStore is not null)
        {
            var now = _timeProvider.GetUtcNow();
            if (_lastPruneUtc is null || now - _lastPruneUtc.Value >= PruneCooldown)
            {
                _lastPruneUtc = now;
                var deleted = _eventStore.PruneOldest(PruneFraction, MinimumPruneRows);
                if (deleted > 0)
                {
                    _logger.LogWarning(
                        "The audit database reached {Bytes} bytes (limit {Limit}); pruned {Deleted} oldest decision rows.",
                        bytes,
                        audit.MaxDatabaseBytes,
                        deleted);
                    bytes = AuditStorageSize.Measure(audit, path);
                }
            }
        }

        _state.Update(bytes, audit.MaxDatabaseBytes);
        _telemetry.UpdateAuditStorage(bytes, audit.MaxDatabaseBytes);
        if (_state.ShedDetail && !_warned)
        {
            _warned = true;
            _logger.LogWarning(
                "The audit database is {Bytes} bytes, over {Percent:P0} of Audit:MaxDatabaseBytes ({Limit}); score breakdowns are no longer stored until it shrinks. Lower Audit:MaxStoredEvents or set Audit:RetentionDays, or raise the limit.",
                bytes,
                AuditStorageState.ShedAtFraction,
                audit.MaxDatabaseBytes);
        }
        else if (!_state.ShedDetail)
        {
            _warned = false;
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                Sample();
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _logger.LogWarning(exception, "Could not measure the audit database size.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
