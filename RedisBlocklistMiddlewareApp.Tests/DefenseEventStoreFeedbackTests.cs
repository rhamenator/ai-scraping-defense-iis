using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class DefenseEventStoreFeedbackTests
{
    [Fact]
    public void SqliteDefenseEventStore_PersistsDecisionIdsAndFeedbackRecords()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "ai-scraping-defense-feedback-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var store = CreateStore(tempDirectory);
            var now = DateTimeOffset.UtcNow;

            store.Add(new DefenseDecision(
                "198.51.100.10",
                "challenged",
                35,
                2,
                "/docs",
                ["signal"],
                "summary",
                now,
                now));

            var decision = Assert.Single(store.GetRecent(10));
            Assert.True(decision.Id > 0);

            var persisted = store.GetById(decision.Id);
            Assert.NotNull(persisted);
            Assert.Equal("198.51.100.10", persisted!.IpAddress);

            var feedback = store.AddFeedback(new DefenseDecisionFeedback(
                0,
                decision.Id,
                decision.IpAddress,
                decision.Action,
                "blocked",
                "confirmed malicious after review",
                "operator@example",
                now.AddMinutes(1)));

            Assert.True(feedback.Id > 0);

            var recentFeedback = Assert.Single(store.GetRecentFeedback(10));
            Assert.Equal(decision.Id, recentFeedback.DecisionId);
            Assert.Equal("challenged", recentFeedback.OriginalAction);
            Assert.Equal("blocked", recentFeedback.UpdatedAction);
            Assert.Equal("operator@example", recentFeedback.Actor);
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    [Fact]
    public void SqliteDefenseEventStore_RejectsFeedbackForUnknownDecision()
    {
        var tempDirectory = Path.Combine(Path.GetTempPath(), "ai-scraping-defense-feedback-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);

        try
        {
            var store = CreateStore(tempDirectory);
            var now = DateTimeOffset.UtcNow;

            Assert.Throws<SqliteException>(() => store.AddFeedback(new DefenseDecisionFeedback(
                0,
                999,
                "198.51.100.99",
                "observed",
                "blocked",
                "invalid decision reference",
                "management_api_key",
                now)));
        }
        finally
        {
            Directory.Delete(tempDirectory, recursive: true);
        }
    }

    private static SqliteDefenseEventStore CreateStore(string rootPath)
    {
        var options = Options.Create(new DefenseEngineOptions
        {
            Audit = new AuditOptions
            {
                DatabasePath = "data/defense-events.db",
                MaxRecentEvents = 100
            }
        });

        return new SqliteDefenseEventStore(options, new TestHostEnvironment(rootPath));
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            ContentRootFileProvider = new PhysicalFileProvider(contentRootPath);
        }

        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "RedisBlocklistMiddlewareApp.Tests";

        public string ContentRootPath { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; }
    }
}
