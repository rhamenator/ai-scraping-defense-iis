using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class SqliteWebhookEventInboxTests : IDisposable
{
    private readonly List<CancellationTokenSource> _sources = [];
    private readonly string _root = Path.Join(Path.GetTempPath(), "asd-inbox-" + Guid.NewGuid().ToString("N"));

    public SqliteWebhookEventInboxTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        foreach (var source in _sources)
        {
            source.Dispose();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task FailedItem_IsRetriedOnlyAfterBackoff()
    {
        var inbox = CreateInbox(maxAttempts: 5, baseMs: 400);
        var id = await inbox.EnqueueAsync(NewEvent(), CancellationToken.None);
        await using var reader = inbox.ReadAllAsync(Cancel(10)).GetAsyncEnumerator(Cancel(10));

        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(id, reader.Current.Id);
        await inbox.AbandonAsync(id, CancellationToken.None);

        var clock = Stopwatch.StartNew();
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(id, reader.Current.Id);
        Assert.True(clock.ElapsedMilliseconds >= 300, $"retried after only {clock.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task ItemThatKeepsFailing_IsDeadLettered_AndStopsBeingRetried()
    {
        var inbox = CreateInbox(maxAttempts: 2, baseMs: 10);
        var poison = await inbox.EnqueueAsync(NewEvent(), CancellationToken.None);
        await using var reader = inbox.ReadAllAsync(Cancel(10)).GetAsyncEnumerator(Cancel(10));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            Assert.True(await reader.MoveNextAsync());
            Assert.Equal(poison, reader.Current.Id);
            await inbox.AbandonAsync(poison, CancellationToken.None);
        }

        var next = await inbox.EnqueueAsync(NewEvent(), CancellationToken.None);
        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(next, reader.Current.Id);
        Assert.Equal("dead", ReadStatus(poison));
    }

    [Fact]
    public async Task CorruptPayload_IsDeadLettered_WithoutStoppingTheReader()
    {
        var inbox = CreateInbox(maxAttempts: 5, baseMs: 10);
        var corrupt = InsertRawRow("{ this is not json");
        var good = await inbox.EnqueueAsync(NewEvent(), CancellationToken.None);
        await using var reader = inbox.ReadAllAsync(Cancel(10)).GetAsyncEnumerator(Cancel(10));

        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(good, reader.Current.Id);
        Assert.Equal("dead", ReadStatus(corrupt));
    }

    [Fact]
    public async Task ExistingDatabaseWithoutRetryColumns_IsMigrated()
    {
        var path = DatabasePath();
        using (var connection = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE webhook_intake_events
                (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    event_type TEXT NOT NULL,
                    reason TEXT NOT NULL,
                    timestamp_utc TEXT NOT NULL,
                    payload_json TEXT NOT NULL,
                    status TEXT NOT NULL,
                    leased_at_utc TEXT NULL
                );
                """;
            command.ExecuteNonQuery();
        }

        var legacy = InsertRawRow(System.Text.Json.JsonSerializer.Serialize(NewEvent()));
        var inbox = CreateInbox(maxAttempts: 5, baseMs: 10);
        await using var reader = inbox.ReadAllAsync(Cancel(10)).GetAsyncEnumerator(Cancel(10));

        Assert.True(await reader.MoveNextAsync());
        Assert.Equal(legacy, reader.Current.Id);
        await inbox.AbandonAsync(legacy, CancellationToken.None);
        Assert.Equal("pending", ReadStatus(legacy));
    }

    private string DatabasePath() => Path.Join(_root, "inbox.db");

    private SqliteWebhookEventInbox CreateInbox(int maxAttempts, int baseMs)
    {
        var options = new DefenseEngineOptions();
        options.Audit.DatabasePath = DatabasePath();
        options.Queue.WebhookMaxAttempts = maxAttempts;
        options.Queue.WebhookRetryBaseMilliseconds = baseMs;
        options.Queue.WebhookRetryMaxMilliseconds = baseMs * 8;
        return new SqliteWebhookEventInbox(Options.Create(options), new TestHostEnvironment(_root));
    }

    private long InsertRawRow(string payload)
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath()};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO webhook_intake_events (event_type, reason, timestamp_utc, payload_json, status)
            VALUES ('test', 'test', '2026-01-01T00:00:00.0000000Z', $payload, 'pending');
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$payload", payload);
        return (long)command.ExecuteScalar()!;
    }

    private string ReadStatus(long id)
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath()};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT status FROM webhook_intake_events WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id);
        return (string)command.ExecuteScalar()!;
    }

    private CancellationToken Cancel(int seconds)
    {
        var source = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        _sources.Add(source);
        return source.Token;
    }

    private static IntakeWebhookEvent NewEvent() => new(
        "test",
        "test reason",
        DateTimeOffset.UtcNow,
        new IntakeWebhookDetails("198.51.100.7", "GET", "/", null, "agent", ["signal"]));

    private sealed class TestHostEnvironment(string contentRootPath) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "RedisBlocklistMiddlewareApp.Tests";

        public string ContentRootPath { get; set; } = contentRootPath;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
