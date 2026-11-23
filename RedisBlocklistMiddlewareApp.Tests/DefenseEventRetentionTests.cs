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

    [Fact]
    public void Prune_DoesNotDeleteRowsWhenIdentityGapsExistAndFewerThanCapRowsRemain()
    {
        using var harness = Harness.Create(maxStoredEvents: 5);
        var store = harness.CreateStore();
        store.Add(Decision("/first"));
        harness.InsertWithExplicitId(1000, "/gapped");

        store.Prune();

        Assert.Equal(2, store.CountStoredEvents());
        var paths = store.GetRecent(10).Select(d => d.Path).ToArray();
        Assert.Contains("/first", paths);
        Assert.Contains("/gapped", paths);
    }

    [Fact]
    public void FeedbackRetention_CapsAndAgesFeedbackAndAllowsUnreferencedDecisionsToPrune()
    {
        Assert.Equal(100_000, new AuditOptions().MaxStoredFeedback);
        using var harness = Harness.Create(maxStoredEvents: 2, retentionDays: 7, maxStoredFeedback: 2);
        var store = harness.CreateStore();
        var oldAt = DateTimeOffset.UtcNow.AddDays(-30);
        store.Add(Decision("/d1", oldAt));
        store.Add(Decision("/d2"));
        store.Add(Decision("/d3"));
        store.Add(Decision("/d4"));

        var all = store.GetRecent(10).OrderBy(d => d.Id).ToArray();
        // Old feedback on /d1 (aged out by RetentionDays) plus 2 recent feedbacks on /d2 and /d3.
        store.AddFeedback(new DefenseDecisionFeedback(0, all[0].Id, all[0].IpAddress, "observed", "blocked", "old", "op", oldAt));
        store.AddFeedback(new DefenseDecisionFeedback(0, all[1].Id, all[1].IpAddress, "observed", "blocked", "f2", "op", DateTimeOffset.UtcNow));
        store.AddFeedback(new DefenseDecisionFeedback(0, all[2].Id, all[2].IpAddress, "observed", "blocked", "f3", "op", DateTimeOffset.UtcNow));

        store.Prune();

        var remainingFeedback = store.GetRecentFeedback(10);
        Assert.Equal(2, remainingFeedback.Count);
        Assert.DoesNotContain(remainingFeedback, f => f.Reason == "old");
        // /d1 lost its feedback so it can be pruned; /d2 stays because its feedback stayed; /d3 and /d4 are the newest 2.
        var keptPaths = store.GetRecent(10).Select(d => d.Path).ToArray();
        Assert.DoesNotContain("/d1", keptPaths);
        Assert.Contains("/d2", keptPaths);
        Assert.Contains("/d3", keptPaths);
        Assert.Contains("/d4", keptPaths);
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
        private readonly int _maxStoredFeedback;

        private Harness(string rootPath, int maxStoredEvents, int retentionDays, int maxStoredFeedback)
        {
            _rootPath = rootPath;
            MaxStoredEvents = maxStoredEvents;
            _retentionDays = retentionDays;
            _maxStoredFeedback = maxStoredFeedback;
        }

        public int MaxStoredEvents { get; set; }

        public static Harness Create(int maxStoredEvents, int retentionDays = 0, int maxStoredFeedback = 100_000)
        {
            var rootPath = Path.Join(Path.GetTempPath(), "ai-scraping-defense-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            return new Harness(rootPath, maxStoredEvents, retentionDays, maxStoredFeedback);
        }

        public void InsertWithExplicitId(long id, string path)
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Join(_rootPath, "events.db")};Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            var now = DateTimeOffset.UtcNow.ToString("O");
            command.CommandText =
                """
                INSERT INTO defense_events
                (id, ip_address, action, score, frequency, path, signals_json, breakdown_json, summary, observed_at_utc, decided_at_utc)
                VALUES ($id, '198.51.100.1', 'observed', 10, 1, $path, '["signal"]', NULL, 'summary', $now, $now);
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$now", now);
            command.ExecuteNonQuery();
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
                    MaxStoredFeedback = _maxStoredFeedback,
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
