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

        // SQLite: the database file plus its write-ahead log and shared-memory files.
        long total = 0;
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            var file = new FileInfo(sqlitePath + suffix);
            if (file.Exists)
            {
                total += file.Length;
            }
        }

        return total;
    }

    private static long Scalar(DbConnection connection, string sql)
    {
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar() ?? 0L);
    }
}

/// <summary>Publishes the audit database size and its limit as metrics, and tells the stores when to shed optional detail.</summary>
public sealed class AuditStorageMonitorService : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);

    private readonly IOptions<DefenseEngineOptions> _options;
    private readonly IHostEnvironment _environment;
    private readonly AuditStorageState _state;
    private readonly DefenseTelemetry _telemetry;
    private readonly ILogger<AuditStorageMonitorService> _logger;
    private bool _warned;

    public AuditStorageMonitorService(
        IOptions<DefenseEngineOptions> options,
        IHostEnvironment environment,
        AuditStorageState state,
        DefenseTelemetry telemetry,
        ILogger<AuditStorageMonitorService> logger)
    {
        _options = options;
        _environment = environment;
        _state = state;
        _telemetry = telemetry;
        _logger = logger;
    }

    public void Sample()
    {
        var audit = _options.Value.Audit;
        var path = Path.IsPathRooted(audit.DatabasePath) ? audit.DatabasePath : Path.Combine(_environment.ContentRootPath, audit.DatabasePath);
        var bytes = AuditStorageSize.Measure(audit, path);
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
