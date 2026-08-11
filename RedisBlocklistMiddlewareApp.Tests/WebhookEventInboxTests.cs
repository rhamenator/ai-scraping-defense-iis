using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class WebhookEventInboxTests
{
    [Fact]
    public async Task Inbox_PersistsQueuedEventsAcrossInstances()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var harness = SqliteInboxHarness.Create();
        var firstInbox = harness.CreateInbox();

        await firstInbox.EnqueueAsync(CreateEvent("198.51.100.10"), cancellationToken);

        var secondInbox = harness.CreateInbox();
        await using var enumerator = secondInbox.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal("198.51.100.10", enumerator.Current.Event.Details.IpAddress);
        Assert.Equal("suspicious_activity_detected", enumerator.Current.Event.EventType);
    }

    [Fact]
    public async Task Abandon_RequeuesClaimedWebhookEvent()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var harness = SqliteInboxHarness.Create();
        var inbox = harness.CreateInbox();
        await inbox.EnqueueAsync(CreateEvent("198.51.100.11"), cancellationToken);

        await using var firstEnumerator = inbox.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        Assert.True(await firstEnumerator.MoveNextAsync());
        var claimed = firstEnumerator.Current;
        await firstEnumerator.DisposeAsync();

        await inbox.AbandonAsync(claimed.Id, cancellationToken);

        await using var secondEnumerator = inbox.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        Assert.True(await secondEnumerator.MoveNextAsync());
        Assert.Equal(claimed.Id, secondEnumerator.Current.Id);
    }

    [Fact]
    public async Task NewInbox_DoesNotStealAnActiveProcessingLease()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var harness = SqliteInboxHarness.Create();
        var firstInbox = harness.CreateInbox();
        await firstInbox.EnqueueAsync(CreateEvent("198.51.100.12"), cancellationToken);

        await using var enumerator = firstInbox.ReadAllAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
        Assert.True(await enumerator.MoveNextAsync());

        _ = harness.CreateInbox();

        using var connection = new SqliteConnection($"Data Source={harness.DatabasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM webhook_intake_events WHERE id = $id";
        command.Parameters.AddWithValue("$id", enumerator.Current.Id);
        Assert.Equal("processing", command.ExecuteScalar());
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

        public string DatabasePath => Path.Combine(_rootPath, "intake.db");

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
