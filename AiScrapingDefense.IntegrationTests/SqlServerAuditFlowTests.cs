using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;
using Testcontainers.MsSql;

namespace AiScrapingDefense.IntegrationTests;

public sealed class SqlServerAuditFlowTests
{
    [Fact]
    public async Task SqlServerAuditStores_RoundTripDecisionsDeliveriesAndWebhookInbox()
    {
        await using var sqlServer = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
        await sqlServer.StartAsync(TestContext.Current.CancellationToken);

        var options = Options.Create(new DefenseEngineOptions
        {
            Audit = new AuditOptions
            {
                Provider = AuditStorageProviders.SqlServer,
                ConnectionString = sqlServer.GetConnectionString(),
                MaxRecentEvents = 20
            }
        });
        var now = DateTimeOffset.UtcNow;

        var decisions = new SqlServerDefenseEventStore(options);
        decisions.Add(new DefenseDecision(
            "198.51.100.88",
            ContainmentActions.Blocked,
            91,
            12,
            "/training",
            ["integration_sql_server"],
            "SQL Server integration decision",
            now,
            now));

        var storedDecision = Assert.Single(decisions.GetRecent(10));
        Assert.Equal("198.51.100.88", storedDecision.IpAddress);
        Assert.Equal(1, decisions.GetMetrics().BlockedCount);

        var feedback = decisions.AddFeedback(new DefenseDecisionFeedback(
            0,
            storedDecision.Id,
            storedDecision.IpAddress,
            storedDecision.Action,
            ContainmentActions.Observed,
            "reviewed",
            "integration-test",
            now));
        Assert.True(feedback.Id > 0);
        Assert.Single(decisions.GetRecentFeedback(10));

        var deliveries = new SqlServerIntakeDeliveryStore(options);
        deliveries.Add(new IntakeDeliveryRecord(
            IntakeDeliveryTypes.Alert,
            IntakeDeliveryChannels.GenericWebhook,
            storedDecision.IpAddress,
            "integration",
            "https://alerts.example.test",
            IntakeDeliveryStatuses.Succeeded,
            "accepted",
            now));
        Assert.Single(deliveries.GetRecent(10));
        Assert.Equal(1, deliveries.GetMetrics().SucceededCount);

        var inbox = new SqlServerWebhookEventInbox(options);
        var eventId = await inbox.EnqueueAsync(
            new IntakeWebhookEvent(
                "model_verdict",
                "integration",
                now,
                new IntakeWebhookDetails(
                    storedDecision.IpAddress,
                    "GET",
                    "/training",
                    string.Empty,
                    "integration-test",
                    ["model_positive"])),
            TestContext.Current.CancellationToken);

        await using var enumerator = inbox
            .ReadAllAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(eventId, enumerator.Current.Id);
        await inbox.CompleteAsync(eventId, TestContext.Current.CancellationToken);
    }
}
