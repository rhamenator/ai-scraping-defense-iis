using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class DefenseEventRetentionTests
{
    [Fact]
    public void Prune_KeepsOnlyTheNewestRowsUpToTheCap()
    {
        using var harness = Harness.Create(maxStoredEvents: 10);
        var store = harness.CreateStore();
        for (var i = 0; i < 25; i++)
        {
            store.Add(Decision($"/item-{i}"));
        }

        store.Prune();

        Assert.Equal(10, store.CountStoredEvents());
        var recent = store.GetRecent(50);
        Assert.Equal("/item-24", recent[0].Path);
        Assert.Equal("/item-15", recent[^1].Path);
    }

    [Fact]
    public void Prune_LeavesTheDashboardTotalsAlone()
    {
        using var harness = Harness.Create(maxStoredEvents: 5);
        var store = harness.CreateStore();
        for (var i = 0; i < 20; i++)
        {
            store.Add(Decision($"/item-{i}"));
        }

        store.Prune();

        Assert.Equal(5, store.CountStoredEvents());
        Assert.Equal(20, store.GetMetrics().TotalDecisions);
    }

    [Fact]
    public void ACapOfZeroDisablesPruningByRows()
    {
        using var harness = Harness.Create(maxStoredEvents: 0);
        var store = harness.CreateStore();
        for (var i = 0; i < 30; i++)
        {
            store.Add(Decision($"/item-{i}"));
        }

        store.Prune();

        Assert.Equal(30, store.CountStoredEvents());
    }

    [Fact]
    public void TheDefaultCapIsGenerousAndNothingIsRemovedBelowIt()
    {
        Assert.Equal(1_000_000, new AuditOptions().MaxStoredEvents);
        Assert.Equal(0, new AuditOptions().RetentionDays);
        using var harness = Harness.Create(maxStoredEvents: new AuditOptions().MaxStoredEvents);
        var store = harness.CreateStore();
        for (var i = 0; i < 40; i++)
        {
            store.Add(Decision($"/item-{i}"));
        }

        store.Prune();

        Assert.Equal(40, store.CountStoredEvents());
    }

    [Fact]
    public void RetentionDays_RemovesOldRowsOnlyWhenConfigured()
    {
        using var kept = Harness.Create(maxStoredEvents: 0, retentionDays: 0);
        var keepStore = kept.CreateStore();
        keepStore.Add(Decision("/old", DateTimeOffset.UtcNow.AddDays(-30)));
        keepStore.Add(Decision("/new", DateTimeOffset.UtcNow));
        keepStore.Prune();
        Assert.Equal(2, keepStore.CountStoredEvents());

        using var aged = Harness.Create(maxStoredEvents: 0, retentionDays: 7);
        var ageStore = aged.CreateStore();
        ageStore.Add(Decision("/old", DateTimeOffset.UtcNow.AddDays(-30)));
        ageStore.Add(Decision("/recent", DateTimeOffset.UtcNow.AddDays(-2)));
        ageStore.Add(Decision("/new", DateTimeOffset.UtcNow));
        ageStore.Prune();

        Assert.Equal(2, ageStore.CountStoredEvents());
        Assert.DoesNotContain(ageStore.GetRecent(10), decision => decision.Path == "/old");
    }

    [Fact]
    public void PruningRunsPeriodicallyWhileEventsArrive()
    {
        using var harness = Harness.Create(maxStoredEvents: 100);
        var store = harness.CreateStore();

        for (var i = 0; i < SqliteDefenseEventStore.PruneEveryInserts; i++)
        {
            store.Add(Decision($"/item-{i}"));
        }

        Assert.Equal(100, store.CountStoredEvents());
    }

    [Fact]
    public void PruningAlsoRunsWhenTheStoreOpens()
    {
        using var harness = Harness.Create(maxStoredEvents: 0);
        var first = harness.CreateStore();
        for (var i = 0; i < 20; i++)
        {
            first.Add(Decision($"/item-{i}"));
        }

        harness.MaxStoredEvents = 6;
        var reopened = harness.CreateStore();

        Assert.Equal(6, reopened.CountStoredEvents());
    }

    [Fact]
    public void PruningKeepsDecisionsThatHaveOperatorFeedbackInsteadOfFailing()
    {
        using var harness = Harness.Create(maxStoredEvents: 3);
        var store = harness.CreateStore();
        for (var i = 0; i < 10; i++)
        {
            store.Add(Decision($"/item-{i}"));
        }

        var reviewed = store.GetRecent(50).Single(d => d.Path == "/item-1");
        store.AddFeedback(new DefenseDecisionFeedback(0, reviewed.Id, reviewed.IpAddress, "observed", "blocked", "reviewed", "operator", DateTimeOffset.UtcNow));

        store.Prune();

        var kept = store.GetRecent(50).Select(d => d.Path).ToArray();
        Assert.Contains("/item-1", kept);
        Assert.Contains("/item-9", kept);
        Assert.Equal(4, store.CountStoredEvents());
        // ...and the store still opens afterwards (a failed prune used to abort start-up).
        Assert.Equal(4, harness.CreateStore().CountStoredEvents());
    }

    [Fact]
    public void AgeRetentionAlsoKeepsDecisionsThatHaveOperatorFeedback()
    {
        using var harness = Harness.Create(maxStoredEvents: 0, retentionDays: 7);
        var store = harness.CreateStore();
        store.Add(Decision("/old-reviewed", DateTimeOffset.UtcNow.AddDays(-30)));
        store.Add(Decision("/old-plain", DateTimeOffset.UtcNow.AddDays(-30)));
        store.Add(Decision("/new", DateTimeOffset.UtcNow));
        var reviewed = store.GetRecent(50).Single(d => d.Path == "/old-reviewed");
        store.AddFeedback(new DefenseDecisionFeedback(0, reviewed.Id, reviewed.IpAddress, "observed", "blocked", "reviewed", "operator", DateTimeOffset.UtcNow));

        store.Prune();

        var kept = store.GetRecent(50).Select(d => d.Path).ToArray();
        Assert.Contains("/old-reviewed", kept);
        Assert.DoesNotContain("/old-plain", kept);
        Assert.Contains("/new", kept);
    }

    [Fact]
    public void DashboardTotalsSurviveARestartAfterPruning()
    {
        using var harness = Harness.Create(maxStoredEvents: 1);
        var store = harness.CreateStore();
        store.Add(Decision("/a"));
        store.Add(Decision("/b", action: "blocked"));
        store.Add(Decision("/c"));
        store.Prune();
        Assert.Equal(1, store.CountStoredEvents());
        Assert.Equal(3, store.GetMetrics().TotalDecisions);

        var reopened = harness.CreateStore();

        Assert.Equal(1, reopened.CountStoredEvents());
        var metrics = reopened.GetMetrics();
        Assert.Equal(3, metrics.TotalDecisions);
        Assert.Equal(1, metrics.BlockedCount);
    }

    [Fact]
    public void ADatabaseWithoutASummaryIsBackfilledFromItsRowsOnce()
    {
        using var harness = Harness.Create(maxStoredEvents: 0);
        var store = harness.CreateStore();
        store.Add(Decision("/a"));
        store.Add(Decision("/b", action: "blocked"));
        harness.DeleteSummaryRow();

        var reopened = harness.CreateStore();

        Assert.Equal(2, reopened.GetMetrics().TotalDecisions);
        Assert.Equal(1, reopened.GetMetrics().BlockedCount);
    }

    private static DefenseDecision Decision(string path, DateTimeOffset? observedAt = null, string action = "observed")
    {
        var at = observedAt ?? DateTimeOffset.UtcNow;
        return new DefenseDecision("198.51.100.1", action, 10, 1, path, ["signal"], "summary", at, at);
    }

    private sealed class Harness : IDisposable
    {
        private readonly string _rootPath;
        private readonly int _retentionDays;

        private Harness(string rootPath, int maxStoredEvents, int retentionDays)
        {
            _rootPath = rootPath;
            MaxStoredEvents = maxStoredEvents;
            _retentionDays = retentionDays;
        }

        public int MaxStoredEvents { get; set; }

        public static Harness Create(int maxStoredEvents, int retentionDays = 0)
        {
            var rootPath = Path.Join(Path.GetTempPath(), "ai-scraping-defense-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            return new Harness(rootPath, maxStoredEvents, retentionDays);
        }

        public void DeleteSummaryRow()
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Join(_rootPath, "events.db")};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM defense_event_summary;";
            command.ExecuteNonQuery();
        }

        public SqliteDefenseEventStore CreateStore()
        {
            var options = Options.Create(new DefenseEngineOptions
            {
                Audit = new AuditOptions
                {
                    DatabasePath = "events.db",
                    MaxStoredEvents = MaxStoredEvents,
                    RetentionDays = _retentionDays
                }
            });
            return new SqliteDefenseEventStore(options, new Environment(_rootPath));
        }

        public void Dispose()
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }
    }

    private sealed class Environment : IHostEnvironment
    {
        public Environment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
        }

        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "RedisBlocklistMiddlewareApp.Tests";

        public string ContentRootPath { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
