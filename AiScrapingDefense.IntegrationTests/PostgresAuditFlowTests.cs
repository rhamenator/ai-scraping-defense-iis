using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;
using Testcontainers.PostgreSql;

namespace AiScrapingDefense.IntegrationTests;

public sealed class PostgresAuditFlowTests
{
    [Fact]
    public async Task PostgresWebhookInbox_WaitsQuietlyWhenEmpty_ThenDeliversAndCompletesAnEvent()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await postgres.StartAsync(TestContext.Current.CancellationToken);

        var options = Options.Create(new DefenseEngineOptions
        {
            Audit = new AuditOptions
            {
                Provider = AuditStorageProviders.Postgres,
                ConnectionString = postgres.GetConnectionString(),
                MaxRecentEvents = 20
            }
        });
        var inbox = new PostgresWebhookEventInbox(options);

        // An empty inbox is the state of every fresh start. Claiming from it used to throw
        // "A command is already in progress" and stop the processing service (and the host with it).
        using (var idle = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken))
        {
            idle.CancelAfter(TimeSpan.FromSeconds(4));
            await using var emptyReader = inbox.ReadAllAsync(idle.Token).GetAsyncEnumerator(idle.Token);
            // It waits for work, then ends cleanly when cancelled; it must not throw.
            Assert.False(await emptyReader.MoveNextAsync());
        }

        var now = DateTimeOffset.UtcNow;
        var eventId = await inbox.EnqueueAsync(
            new IntakeWebhookEvent(
                "model_verdict",
                "integration",
                now,
                new IntakeWebhookDetails("198.51.100.99", "GET", "/x", string.Empty, "integration-test", ["model_positive"])),
            TestContext.Current.CancellationToken);

        await using var enumerator = inbox
            .ReadAllAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await enumerator.MoveNextAsync());
        Assert.Equal(eventId, enumerator.Current.Id);
        await inbox.CompleteAsync(eventId, TestContext.Current.CancellationToken);
    }
}
