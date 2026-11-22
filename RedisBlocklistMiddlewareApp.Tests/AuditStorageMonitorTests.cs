using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class AuditStorageMonitorTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "asd-audit-size-" + Guid.NewGuid().ToString("N"));

    public AuditStorageMonitorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(1_000_000, 0, false)]
    [InlineData(899, 1000, false)]
    [InlineData(900, 1000, true)]
    [InlineData(5000, 1000, true)]
    public void DetailIsShedFromNinetyPercentOfTheLimitAndNeverWithoutOne(long bytes, long limit, bool shed)
    {
        var state = new AuditStorageState();

        state.Update(bytes, limit);

        Assert.Equal(shed, state.ShedDetail);
        Assert.Equal(bytes, state.Bytes);
    }

    [Fact]
    public void DetailComesBackWhenTheDatabaseShrinks()
    {
        var state = new AuditStorageState();
        state.Update(950, 1000);
        Assert.True(state.ShedDetail);

        state.Update(100, 1000);

        Assert.False(state.ShedDetail);
    }

    [Fact]
    public void SqliteSizeIncludesTheWriteAheadLogAndSharedMemoryFiles()
    {
        var path = Path.Join(_root, "events.db");
        File.WriteAllBytes(path, new byte[1000]);
        File.WriteAllBytes(path + "-wal", new byte[300]);
        File.WriteAllBytes(path + "-shm", new byte[20]);

        Assert.Equal(1320, AuditStorageSize.Measure(new AuditOptions { Provider = AuditStorageProviders.Sqlite }, path));
        Assert.Equal(0, AuditStorageSize.Measure(new AuditOptions { Provider = AuditStorageProviders.Sqlite }, Path.Join(_root, "missing.db")));
    }

    [Fact]
    public void TheStoreStopsSavingBreakdownsOnceTheStateSaysToShedAndSavesThemAgainAfter()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new DefenseEngineOptions
        {
            Audit = new AuditOptions { DatabasePath = "events.db", MaxRecentEvents = 100, MaxDatabaseBytes = 1000 }
        });
        var state = new AuditStorageState();
        var store = new SqliteDefenseEventStore(options, new TestHostEnvironment(_root), state);
        DefenseDecision Decision(string ip) => new(
            ip, ContainmentActions.Blocked, 90, 1, "/p", ["s"], "summary", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            new DefenseScoreBreakdown(90, 0, 90, true, [new DefenseScoreContribution("edge", 90, ["s"], "why")]));

        store.Add(Decision("198.51.100.1"));
        state.Update(2000, 1000);
        store.Add(Decision("198.51.100.2"));
        state.Update(10, 1000);
        store.Add(Decision("198.51.100.3"));

        var byIp = store.GetRecent(10).ToDictionary(d => d.IpAddress);
        Assert.NotNull(byIp["198.51.100.1"].Breakdown);
        Assert.Null(byIp["198.51.100.2"].Breakdown);
        Assert.NotNull(byIp["198.51.100.3"].Breakdown);
        Assert.Equal(3, byIp.Count);
    }

    [Fact]
    public void TheMonitorMeasuresTheDatabaseAndUpdatesTheState()
    {
        var path = Path.Join(_root, "events.db");
        File.WriteAllBytes(path, new byte[950]);
        var options = Microsoft.Extensions.Options.Options.Create(new DefenseEngineOptions
        {
            Audit = new AuditOptions { Provider = AuditStorageProviders.Sqlite, DatabasePath = "events.db", MaxDatabaseBytes = 1000 }
        });
        var state = new AuditStorageState();
        var monitor = new AuditStorageMonitorService(
            options, new TestHostEnvironment(_root), state, new DefenseTelemetry(options), NullLogger<AuditStorageMonitorService>.Instance);

        monitor.Sample();

        Assert.Equal(950, state.Bytes);
        Assert.True(state.ShedDetail);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRootPath) => ContentRootPath = contentRootPath;

        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "RedisBlocklistMiddlewareApp.Tests";

        public string ContentRootPath { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
