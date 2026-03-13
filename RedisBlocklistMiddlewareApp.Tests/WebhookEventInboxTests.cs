using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class WebhookEventInboxTests
{
    [Fact]
    public async Task Inbox_PersistsQueuedEventsAcrossInstances()
    {
        using var harness = SqliteInboxHarness.Create();
        var firstInbox = harness.CreateInbox();

        await firstInbox.EnqueueAsync(CreateEvent("198.51.100.10"), CancellationToken.None);

        var secondInbox = harness.CreateInbox();
        await using var enumerator = secondInbox.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("198.51.100.10", enumerator.Current.Event.Details.IpAddress);
        Assert.Equal("suspicious_activity_detected", enumerator.Current.Event.EventType);
    }

    [Fact]
    public async Task Abandon_RequeuesClaimedWebhookEvent()
    {
        using var harness = SqliteInboxHarness.Create();
        var inbox = harness.CreateInbox();
        await inbox.EnqueueAsync(CreateEvent("198.51.100.11"), CancellationToken.None);

        await using var firstEnumerator = inbox.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await firstEnumerator.MoveNextAsync());
        var claimed = firstEnumerator.Current;
        await firstEnumerator.DisposeAsync();

        await inbox.AbandonAsync(claimed.Id, CancellationToken.None);

        await using var secondEnumerator = inbox.ReadAllAsync(CancellationToken.None).GetAsyncEnumerator();
        Assert.True(await secondEnumerator.MoveNextAsync());
        Assert.Equal(claimed.Id, secondEnumerator.Current.Id);
    }

    private static IntakeWebhookEvent CreateEvent(string ipAddress)
    {
        return new IntakeWebhookEvent(
            "suspicious_activity_detected",
            "High Combined Score (0.95)",
            DateTimeOffset.UtcNow,
            new IntakeWebhookDetails(
                ipAddress,
                "GET",
                "/test",
                string.Empty,
                "test-agent",
                ["signal"]));
    }

    private sealed class SqliteInboxHarness : IDisposable
    {
        private readonly string _rootPath;

        private SqliteInboxHarness(string rootPath)
        {
            _rootPath = rootPath;
            Options = Microsoft.Extensions.Options.Options.Create(new DefenseEngineOptions
            {
                Audit = new AuditOptions
                {
                    DatabasePath = "intake.db"
                }
            });
        }

        public IOptions<DefenseEngineOptions> Options { get; }

        public static SqliteInboxHarness Create()
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "ai-scraping-defense-intake-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            return new SqliteInboxHarness(rootPath);
        }

        public SqliteWebhookEventInbox CreateInbox()
        {
            return new SqliteWebhookEventInbox(Options, new TestHostEnvironment(_rootPath));
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
