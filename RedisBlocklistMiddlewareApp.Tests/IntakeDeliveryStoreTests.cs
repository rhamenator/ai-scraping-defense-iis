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

    private const string Canary = "CANARY-WEBHOOK-TOKEN-9f3a";

    [Fact]
    public void Add_NeverPersistsAWebhookCredentialAndGetRecentNeverReturnsOne()
    {
        using var harness = SqliteStoreHarness.Create();
        var store = harness.CreateStore();
        store.Add(CreateRecord("198.51.100.40", IntakeDeliveryStatuses.Failed, $"failed for https://hooks.example.test/services/{Canary}")
            with
        { Target = $"https://hooks.example.test/services/{Canary}?token={Canary}" });

        var recent = store.GetRecent(10);

        Assert.DoesNotContain(Canary, recent[0].Target);
        Assert.DoesNotContain(Canary, recent[0].Detail);
        Assert.DoesNotContain(Canary, File.ReadAllText(harness.DatabasePath, System.Text.Encoding.Latin1));
    }

    [Fact]
    public void Opening_TheStore_ScrubsRowsWrittenByEarlierReleases()
    {
        using var harness = SqliteStoreHarness.Create();
        harness.CreateStore(); // creates the schema
        InsertRawRow(harness.DatabasePath, $"https://hooks.slack.example.test/services/T000/B000/{Canary}", $"POST https://hooks.slack.example.test/services/T000/B000/{Canary} returned 500");
        InsertRawRow(harness.DatabasePath, "ops@example.test", "Alert delivered successfully.");
        Assert.Contains(Canary, File.ReadAllText(harness.DatabasePath, System.Text.Encoding.Latin1));

        var reopened = harness.CreateStore();

        // SQLite may keep freed pages; assert on what the store returns and on the live rows instead of raw bytes.
        var rows = ReadRawRows(harness.DatabasePath);
        Assert.All(rows, row => Assert.DoesNotContain(Canary, row.Target + row.Detail));
        Assert.Contains(rows, row => row.Target.StartsWith("https://hooks.slack.example.test (id=", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Target == "ops@example.test" && row.Detail == "Alert delivered successfully.");
        Assert.All(reopened.GetRecent(10), record => Assert.DoesNotContain(Canary, record.Target + record.Detail));
    }

    [Fact]
    public void Scrubbing_IsIdempotentAndLeavesCleanRowsAlone()
    {
        using var harness = SqliteStoreHarness.Create();
        harness.CreateStore();
        InsertRawRow(harness.DatabasePath, $"https://hooks.example.test/{Canary}", "first");
        harness.CreateStore();
        var afterFirst = ReadRawRows(harness.DatabasePath);

        harness.CreateStore();

        Assert.Equal(afterFirst, ReadRawRows(harness.DatabasePath));
    }

    private static void InsertRawRow(string databasePath, string target, string detail)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO intake_delivery_events (delivery_type, channel, ip_address, reason, target, status, detail, attempted_at_utc) " +
            "VALUES ('alert', 'slack', '198.51.100.50', 'legacy row', $target, 'failed', $detail, $at);";
        command.Parameters.AddWithValue("$target", target);
        command.Parameters.AddWithValue("$detail", detail);
        command.Parameters.AddWithValue("$at", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static List<(string Target, string Detail)> ReadRawRows(string databasePath)
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={databasePath};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT target, detail FROM intake_delivery_events ORDER BY id;";
        using var reader = command.ExecuteReader();
        var rows = new List<(string, string)>();
        while (reader.Read())
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows;
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

        public string DatabasePath => Path.Join(_rootPath, "events.db");

        public static SqliteStoreHarness Create(int maxRecentEvents = 500)
        {
            var rootPath = Path.Join(Path.GetTempPath(), "ai-scraping-defense-tests", Guid.NewGuid().ToString("N"));
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
