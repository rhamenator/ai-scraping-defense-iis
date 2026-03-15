using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class IntakeDeliveryStoreTests
{
    [Fact]
    public void GetRecent_ReturnsMostRecentAttemptsFirst()
    {
        using var harness = SqliteStoreHarness.Create();
        var store = harness.CreateStore();
        store.Add(CreateRecord("198.51.100.1", IntakeDeliveryStatuses.Succeeded, "first"));
        store.Add(CreateRecord("198.51.100.2", IntakeDeliveryStatuses.Failed, "second"));
        store.Add(CreateRecord("198.51.100.3", IntakeDeliveryStatuses.Succeeded, "third"));

        var recent = store.GetRecent(2);

        Assert.Equal(2, recent.Count);
        Assert.Equal("198.51.100.3", recent[0].IpAddress);
        Assert.Equal("198.51.100.2", recent[1].IpAddress);
    }

    [Fact]
    public void GetMetrics_ReturnsAggregatedAttemptCounts()
    {
        using var harness = SqliteStoreHarness.Create();
        var store = harness.CreateStore();
        store.Add(CreateRecord("198.51.100.11", IntakeDeliveryStatuses.Succeeded, "ok"));
        store.Add(CreateRecord("198.51.100.12", IntakeDeliveryStatuses.Failed, "fail"));
        store.Add(CreateRecord("198.51.100.13", IntakeDeliveryStatuses.Skipped, "skip"));

        var metrics = store.GetMetrics();

        Assert.Equal(3, metrics.TotalAttempts);
        Assert.Equal(1, metrics.SucceededCount);
        Assert.Equal(1, metrics.FailedCount);
        Assert.Equal(1, metrics.SkippedCount);
        Assert.NotNull(metrics.LatestAttemptAtUtc);
    }

    [Fact]
    public void Store_PersistsAttemptsAcrossInstances()
    {
        using var harness = SqliteStoreHarness.Create();
        harness.CreateStore().Add(CreateRecord("198.51.100.25", IntakeDeliveryStatuses.Succeeded, "persisted"));

        var recent = harness.CreateStore().GetRecent(10);

        Assert.Single(recent);
        Assert.Equal("198.51.100.25", recent[0].IpAddress);
        Assert.Equal("persisted", recent[0].Detail);
    }

    private static IntakeDeliveryRecord CreateRecord(string ipAddress, string status, string detail)
    {
        return new IntakeDeliveryRecord(
            IntakeDeliveryTypes.Alert,
            IntakeDeliveryChannels.GenericWebhook,
            ipAddress,
            "AI scraper detected",
            "https://alerts.example.test",
            status,
            detail,
            DateTimeOffset.UtcNow);
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

        public SqliteIntakeDeliveryStore CreateStore()
        {
            return new SqliteIntakeDeliveryStore(Options, new TestHostEnvironment(_rootPath));
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
