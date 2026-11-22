using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;
using Testcontainers.MsSql;
using Testcontainers.PostgreSql;

namespace AiScrapingDefense.IntegrationTests;

/// <summary>Row-cap and age retention on the PostgreSQL and SQL Server audit tables (#197).</summary>
public sealed class RelationalRetentionTests
{
    [Fact]
    public async Task Postgres_RowCapAndAgeRetentionKeepFeedbackDecisionsAndTotals()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await postgres.StartAsync(TestContext.Current.CancellationToken);

        Exercise(AuditStorageProviders.Postgres, postgres.GetConnectionString(), o => new PostgresDefenseEventStore(o), o => new PostgresIntakeDeliveryStore(o));
    }

    [Fact]
    public async Task SqlServer_RowCapAndAgeRetentionKeepFeedbackDecisionsAndTotals()
    {
        await using var sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await sqlServer.StartAsync(TestContext.Current.CancellationToken);

        Exercise(AuditStorageProviders.SqlServer, sqlServer.GetConnectionString(), o => new SqlServerDefenseEventStore(o), o => new SqlServerIntakeDeliveryStore(o));
    }

    private static void Exercise(
        string provider,
        string connectionString,
        Func<IOptions<DefenseEngineOptions>, RelationalDefenseEventStore> createDecisions,
        Func<IOptions<DefenseEngineOptions>, RelationalIntakeDeliveryStore> createDeliveries)
    {
        IOptions<DefenseEngineOptions> Options(int cap, int days) => Microsoft.Extensions.Options.Options.Create(new DefenseEngineOptions
        {
            Audit = new AuditOptions { Provider = provider, ConnectionString = connectionString, MaxRecentEvents = 5000, MaxStoredEvents = cap, RetentionDays = days }
        });

        var now = DateTimeOffset.UtcNow;
        var unlimited = createDecisions(Options(0, 0));
        for (var i = 0; i < 30; i++)
        {
            var at = i < 5 ? now.AddDays(-40) : now;
            unlimited.Add(new DefenseDecision($"198.51.100.{i + 1}", ContainmentActions.Blocked, 90, 1, "/p", ["t"], $"decision {i}", at, at));
        }

        // An operator gave feedback on the very first (oldest) decision: it must survive both rules.
        var oldest = unlimited.GetRecent(5000).OrderBy(d => d.Id).First();
        unlimited.AddFeedback(new DefenseDecisionFeedback(0, oldest.Id, oldest.IpAddress, oldest.Action, ContainmentActions.Observed, "reviewed", "test", now));
        Assert.Equal(30, unlimited.GetRecent(5000).Count);

        // Age: the four other 40-day-old decisions go, the one with feedback stays.
        var aged = createDecisions(Options(0, 30));
        Assert.Equal(26, aged.GetRecent(5000).Count);
        Assert.Contains(aged.GetRecent(5000), d => d.Id == oldest.Id);

        // Cap: keep only the newest 10 (plus the protected one).
        var capped = createDecisions(Options(10, 0));
        var kept = capped.GetRecent(5000);
        Assert.Equal(11, kept.Count);
        Assert.Contains(kept, d => d.Id == oldest.Id);
        Assert.Equal("decision 29", kept.OrderByDescending(d => d.Id).First().Summary);

        // The dashboard totals are not reduced by retention.
        Assert.Equal(30, capped.GetMetrics().TotalDecisions);

        // Deliveries: cap applies too, with the newest rows kept.
        var deliveries = createDeliveries(Options(0, 0));
        for (var i = 0; i < 25; i++)
        {
            deliveries.Add(new IntakeDeliveryRecord(IntakeDeliveryTypes.Alert, IntakeDeliveryChannels.GenericWebhook, "198.51.100.9", "r", "https://alerts.example.test", IntakeDeliveryStatuses.Succeeded, $"d{i}", now.AddSeconds(i)));
        }

        var cappedDeliveries = createDeliveries(Options(8, 0));
        var remaining = cappedDeliveries.GetRecent(5000);
        Assert.Equal(8, remaining.Count);
        Assert.Equal("d24", remaining[0].Detail);
    }
}
