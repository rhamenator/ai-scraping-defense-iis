using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class IncidentAlertTests
{
    [Fact]
    public void DistributedSignal_RequiresRecentDistributedBlocks()
    {
        var now = DateTimeOffset.UtcNow;
        var options = new CloudflareOptions
        {
            MinimumRecentDecisions = 3,
            MinimumBlockedRate = 0.5,
            MinimumDistinctBlockedOrigins = 2,
            AttackWindowMinutes = 5
        };
        var recent = new[]
        {
            Decision("198.51.100.1", now),
            Decision("198.51.100.2", now),
            Decision("198.51.100.3", now)
        };

        var signal = DistributedAttackSignalEvaluator.Evaluate(recent, options, now);
        Assert.NotNull(signal);
        Assert.Equal(3, signal.DecisionCount);
        Assert.Equal(3, signal.DistinctBlockedOrigins);
        Assert.Null(DistributedAttackSignalEvaluator.Evaluate(
            recent.Select(item => item with { DecidedAtUtc = now.AddMinutes(-6) }).ToArray(),
            options,
            now));
        Assert.Null(DistributedAttackSignalEvaluator.Evaluate(
            recent.Select(item => item with { IpAddress = "198.51.100.1" }).ToArray(),
            options,
            now));
    }

    [Fact]
    public async Task OriginHealthChecker_OnlyMarksHealthyBackup()
    {
        var options = new DefenseEngineOptions();
        options.IncidentAlerts.PrimaryOriginHealthUrl = "http://origin.test/primary";
        options.IncidentAlerts.BackupOriginHealthUrl = "http://origin.test/backup";
        var checker = new OriginHealthChecker(
            Options.Create(options),
            new FakeHttpClientFactory(new RecordingHandler()));

        var result = await checker.CheckAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Configured);
        Assert.False(result.PrimaryHealthy);
        Assert.True(result.BackupHealthy);
    }

    [Fact]
    public async Task IncidentWebhook_ContainsManualChoicesAndEvidence()
    {
        var options = new DefenseEngineOptions();
        options.Intake.Alerting.GenericWebhook.Enabled = true;
        options.Intake.Alerting.GenericWebhook.Url = "http://sink.test/alert";
        var handler = new RecordingHandler();
        var dispatcher = new OperatorIncidentAlertDispatcher(
            Options.Create(options),
            new FakeHttpClientFactory(handler),
            new NoopSmtpSender());
        var now = DateTimeOffset.UtcNow;
        var alert = new OperatorIncidentAlert(
            new DistributedAttackSignal(now.AddMinutes(-5), now, 50, 40, 30, 0.8),
            new OriginHealthSnapshot(true, false, true, "primary.test", "backup.test"),
            ["Manually enable Cloudflare Under Attack Mode.", "Manually fail over to backup.test."]);

        var deliveries = await dispatcher.DispatchAsync(alert, TestContext.Current.CancellationToken);

        Assert.Equal(IntakeDeliveryStatuses.Succeeded, Assert.Single(deliveries).Status);
        Assert.NotNull(handler.Body);
        using var body = JsonDocument.Parse(handler.Body);
        Assert.Equal("AI_DEFENSE_DISTRIBUTED_ATTACK", body.RootElement.GetProperty("alert_type").GetString());
        Assert.Equal(30, body.RootElement.GetProperty("distinct_blocked_origins").GetInt32());
        Assert.Equal(2, body.RootElement.GetProperty("operator_choices").GetArrayLength());
        Assert.False(body.RootElement.GetProperty("automatic_cloudflare_change").GetBoolean());
        Assert.False(body.RootElement.GetProperty("automatic_failover").GetBoolean());
    }

    private static DefenseDecision Decision(string ip, DateTimeOffset now) => new(
        ip, "blocked", 90, 1, "/docs", ["synthetic"], "test", now, now);

    private sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(request.RequestUri?.AbsolutePath == "/primary"
                ? HttpStatusCode.ServiceUnavailable
                : HttpStatusCode.OK);
        }
    }

    private sealed class NoopSmtpSender : ISmtpAlertSender
    {
        public Task SendAsync(
            string host,
            int port,
            string username,
            string password,
            bool useTls,
            string from,
            IReadOnlyList<string> to,
            string subject,
            string body,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
