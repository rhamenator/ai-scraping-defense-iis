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

    [Fact]
    public void SqliteLiveBytesReflectsFreelistAfterPruningEvenWithoutVacuum()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new DefenseEngineOptions
        {
            Audit = new AuditOptions { Provider = AuditStorageProviders.Sqlite, DatabasePath = "events.db", MaxRecentEvents = 5000, MaxStoredEvents = 0 }
        });
        var store = new SqliteDefenseEventStore(options, new TestHostEnvironment(_root));
        var payload = new string('x', 500);
        for (var i = 0; i < 200; i++)
        {
            store.Add(new DefenseDecision($"198.51.100.{i % 250}", ContainmentActions.Observed, 10, 1, $"/p/{i}", ["s"], payload, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }

        var path = Path.Join(_root, "events.db");
        var before = AuditStorageSize.Measure(options.Value.Audit, path);
        Assert.True(before > 0);

        var deleted = store.PruneOldest(0.5, 100);
        Assert.Equal(100, deleted);

        var after = AuditStorageSize.Measure(options.Value.Audit, path);
        Assert.True(after < before, $"Expected live bytes to drop after pruning (before={before}, after={after}).");
    }

    [Fact]
    public void TheMonitorPrunesOldestRowsAtLimitAndRespectsCooldown()
    {
        var options = Microsoft.Extensions.Options.Options.Create(new DefenseEngineOptions
        {
            Audit = new AuditOptions { Provider = AuditStorageProviders.Sqlite, DatabasePath = "events.db", MaxRecentEvents = 5000, MaxStoredEvents = 0, MaxDatabaseBytes = 10_000 }
        });
        var state = new AuditStorageState();
        var env = new TestHostEnvironment(_root);
        var store = new SqliteDefenseEventStore(options, env, state);
        var payload = new string('y', 400);
        for (var i = 0; i < 1200; i++)
        {
            store.Add(new DefenseDecision($"198.51.100.{i % 250}", ContainmentActions.Observed, 10, 1, $"/p/{i}", ["s"], payload, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }

        var reviewed = store.GetRecent(5000).OrderBy(d => d.Id).First();
        store.AddFeedback(new DefenseDecisionFeedback(0, reviewed.Id, reviewed.IpAddress, reviewed.Action, ContainmentActions.Blocked, "keep", "op", DateTimeOffset.UtcNow));

        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var monitor = new AuditStorageMonitorService(
            options, env, state, new DefenseTelemetry(options), NullLogger<AuditStorageMonitorService>.Instance, store, clock);

        monitor.Sample();

        // Floor is 1000 rows, except the 1 row with feedback stays: 1200 - 1000 = 200 remaining.
        var afterFirst = store.GetRecent(5000);
        Assert.Equal(200, afterFirst.Count);
        Assert.Contains(afterFirst, d => d.Id == reviewed.Id);

        // Add more rows and sample again before cooldown: nothing pruned.
        for (var i = 0; i < 100; i++)
        {
            store.Add(new DefenseDecision("198.51.100.9", ContainmentActions.Observed, 10, 1, $"/extra/{i}", ["s"], payload, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }

        clock.Advance(TimeSpan.FromMinutes(1));
        monitor.Sample();
        Assert.Equal(300, store.GetRecent(5000).Count);

        // Once the cooldown passes and size is still >= MaxDatabaseBytes, it prunes again.
        clock.Advance(AuditStorageMonitorService.PruneCooldown);
        monitor.Sample();
        Assert.Single(store.GetRecent(5000)); // only the feedback-protected row remains
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public FakeTimeProvider(DateTimeOffset utcNow) => _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
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
