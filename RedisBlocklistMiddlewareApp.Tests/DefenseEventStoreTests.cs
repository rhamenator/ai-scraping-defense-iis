using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Services;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class DefenseEventStoreTests
{
    [Fact]
    public void GetRecent_ReturnsMostRecentEventsFirst()
    {
        using var harness = SqliteStoreHarness.Create();
        var store = harness.CreateStore();
        store.Add(CreateDecision("198.51.100.1", "/first"));
        store.Add(CreateDecision("198.51.100.2", "/second"));
        store.Add(CreateDecision("198.51.100.3", "/third"));

        var recent = store.GetRecent(2);

        Assert.Equal(2, recent.Count);
        Assert.Equal("/third", recent[0].Path);
        Assert.Equal("/second", recent[1].Path);
    }

    [Fact]
    public void GetRecent_ClampsToMaxCapacity()
    {
        using var harness = SqliteStoreHarness.Create(maxRecentEvents: 200);
        var store = harness.CreateStore();

        for (var i = 0; i < 205; i++)
        {
            store.Add(CreateDecision($"198.51.100.{i}", $"/item-{i}"));
        }

        var recent = store.GetRecent(500);

        Assert.Equal(200, recent.Count);
        Assert.Equal("/item-204", recent[0].Path);
        Assert.Equal("/item-5", recent[^1].Path);
    }

    [Fact]
    public void Store_PersistsEventsAcrossInstances()
    {
        using var harness = SqliteStoreHarness.Create();
        var firstStore = harness.CreateStore();
        firstStore.Add(CreateDecision("198.51.100.9", "/persisted"));

        var secondStore = harness.CreateStore();
        var recent = secondStore.GetRecent(10);

        Assert.Single(recent);
        Assert.Equal("/persisted", recent[0].Path);
        Assert.Equal("198.51.100.9", recent[0].IpAddress);
    }

    [Fact]
    public void GetMetrics_ReturnsAggregatedDecisionCounts()
    {
        using var harness = SqliteStoreHarness.Create();
        var store = harness.CreateStore();
        store.Add(CreateDecision("198.51.100.1", "/observed"));
        store.Add(CreateDecision("198.51.100.2", "/blocked", action: "blocked"));

        var metrics = store.GetMetrics();

        Assert.Equal(2, metrics.TotalDecisions);
        Assert.Equal(1, metrics.BlockedCount);
        Assert.Equal(1, metrics.ObservedCount);
        Assert.NotNull(metrics.LatestDecisionAtUtc);
    }

    [Fact]
    public void Store_RoundTripsDecisionBreakdown()
    {
        using var harness = SqliteStoreHarness.Create();
        var store = harness.CreateStore();
        store.Add(CreateDecision("198.51.100.50", "/breakdown"));

        var recent = store.GetRecent(1);

        Assert.Single(recent);
        var breakdown = recent[0].Breakdown;
        Assert.NotNull(breakdown);
        Assert.Equal(10, breakdown!.TotalScore);
        Assert.Equal("blocked", breakdown.Containment!.Action);
        Assert.Equal("queued_analysis_threshold", breakdown.Containment.Reason);
        Assert.Equal("Remote", breakdown.Routing!.EffectiveRoute);
        Assert.Contains(breakdown.AdapterVerdicts!, verdict => verdict.Classification == "MALICIOUS_BOT");
        Assert.Contains(breakdown.ContributorNames, contributor => contributor == "test");
        Assert.Contains(breakdown.Contributions, contribution => contribution.Source == "test");
    }

    private static DefenseDecision CreateDecision(string ipAddress, string path, string action = "observed")
    {
        var observedAt = DateTimeOffset.UtcNow;
        return new DefenseDecision(
            ipAddress,
            action,
            10,
            1,
            path,
            ["signal"],
            "summary",
            observedAt,
            observedAt,
            new DefenseScoreBreakdown(
                5,
                5,
                10,
                false,
                [
                    new DefenseScoreContribution(
                        "test",
                        10,
                        ["signal"],
                        "Test contribution.")
                ],
                [
                    new DefenseAdapterVerdict(
                        "test_adapter",
                        ThreatModelRoutes.Remote,
                        "MALICIOUS_BOT",
                        true,
                        10,
                        true,
                        ["signal"],
                        "Test adapter verdict.")
                ],
                new DefenseRoutingDetails(
                    ThreatModelRoutes.Remote,
                    ThreatModelRoutes.Remote,
                    false,
                    ["Remote:test_adapter"],
                    ["Remote:test_adapter"]),
                new DefenseContainmentDetails(
                    ContainmentActions.Blocked,
                    "queued_analysis_threshold",
                    true)));
    }

    private sealed class SqliteStoreHarness : IDisposable
    {
        private readonly string _rootPath;

        private SqliteStoreHarness(string rootPath, int maxRecentEvents)
        {
            _rootPath = rootPath;
            Options = Microsoft.Extensions.Options.Options.Create(new DefenseEngineOptions
            {
                Audit = new AuditOptions
                {
                    DatabasePath = "events.db",
                    MaxRecentEvents = maxRecentEvents
                }
            });
        }

        public IOptions<DefenseEngineOptions> Options { get; }

        public static SqliteStoreHarness Create(int maxRecentEvents = 500)
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "ai-scraping-defense-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            return new SqliteStoreHarness(rootPath, maxRecentEvents);
        }

        public SqliteDefenseEventStore CreateStore()
        {
            return new SqliteDefenseEventStore(Options, new TestHostEnvironment(_rootPath));
        }

        public void Dispose()
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
        }

        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "RedisBlocklistMiddlewareApp.Tests";

        public string ContentRootPath { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
