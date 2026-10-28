using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed record OperatorIncidentAlert(
    DistributedAttackSignal Signal,
    OriginHealthSnapshot Origins,
    IReadOnlyList<string> OperatorChoices);

public interface IOperatorIncidentAlertDispatcher
{
    Task<IReadOnlyList<IntakeDeliveryRecord>> DispatchAsync(
        OperatorIncidentAlert alert,
        CancellationToken cancellationToken);
}

public sealed class OperatorIncidentAlertDispatcher : IOperatorIncidentAlertDispatcher
{
    private readonly IntakeAlertingOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISmtpAlertSender _smtpAlertSender;

    public OperatorIncidentAlertDispatcher(
        IOptions<DefenseEngineOptions> options,
        IHttpClientFactory httpClientFactory,
        ISmtpAlertSender smtpAlertSender)
    {
        _options = options.Value.Intake.Alerting;
        _httpClientFactory = httpClientFactory;
        _smtpAlertSender = smtpAlertSender;
    }

    public async Task<IReadOnlyList<IntakeDeliveryRecord>> DispatchAsync(
        OperatorIncidentAlert alert,
        CancellationToken cancellationToken)
    {
        var deliveries = await Task.WhenAll(
            SendGenericWebhookAsync(alert, cancellationToken),
            SendSlackAsync(alert, cancellationToken),
            SendSmtpAsync(alert, cancellationToken));
        return deliveries.Where(delivery => delivery is not null)
            .Cast<IntakeDeliveryRecord>()
            .ToArray();
    }

    private Task<IntakeDeliveryRecord?> SendGenericWebhookAsync(
        OperatorIncidentAlert alert,
        CancellationToken cancellationToken)
    {
        var options = _options.GenericWebhook;
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.Url))
        {
            return Task.FromResult<IntakeDeliveryRecord?>(null);
        }

        var payload = new
        {
            alert_type = "AI_DEFENSE_DISTRIBUTED_ATTACK",
            severity = "high",
            detected_at_utc = alert.Signal.DetectedAtUtc,
            window_start_utc = alert.Signal.WindowStartUtc,
            decision_count = alert.Signal.DecisionCount,
            blocked_count = alert.Signal.BlockedCount,
            blocked_rate = alert.Signal.BlockedRate,
            distinct_blocked_origins = alert.Signal.DistinctBlockedOrigins,
            origin_health = alert.Origins,
            operator_choices = alert.OperatorChoices,
            automatic_cloudflare_change = false,
            automatic_failover = false
        };
        return SendHttpAsync(
            IntakeDeliveryChannels.GenericWebhook,
            options.Url,
            payload,
            options.AuthorizationHeaderValue,
            options.TimeoutSeconds,
            cancellationToken);
    }

    private Task<IntakeDeliveryRecord?> SendSlackAsync(
        OperatorIncidentAlert alert,
        CancellationToken cancellationToken)
    {
        var options = _options.Slack;
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.WebhookUrl))
        {
            return Task.FromResult<IntakeDeliveryRecord?>(null);
        }

        return SendHttpAsync(
            IntakeDeliveryChannels.Slack,
            options.WebhookUrl,
            new { text = BuildMessage(alert) },
            string.Empty,
            options.TimeoutSeconds,
            cancellationToken);
    }

    private async Task<IntakeDeliveryRecord?> SendSmtpAsync(
        OperatorIncidentAlert alert,
        CancellationToken cancellationToken)
    {
        var options = _options.Smtp;
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.Host) ||
            string.IsNullOrWhiteSpace(options.From) || options.To.Length == 0)
        {
            return null;
        }

        var attemptedAtUtc = DateTimeOffset.UtcNow;
        var target = string.Join(",", options.To);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));
            await _smtpAlertSender.SendAsync(
                options.Host,
                options.Port,
                options.Username,
                options.Password,
                options.UseTls,
                options.From,
                options.To,
                "[AI Defense Incident] Distributed attack detected",
                BuildMessage(alert),
                timeout.Token);
            return Record(IntakeDeliveryChannels.Smtp, target, true, "Incident alert delivered.", attemptedAtUtc);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Record(IntakeDeliveryChannels.Smtp, target, false, ex.Message, attemptedAtUtc);
        }
    }

    private async Task<IntakeDeliveryRecord?> SendHttpAsync(
        string channel,
        string url,
        object payload,
        string authorization,
        int timeoutSeconds,
        CancellationToken cancellationToken)
    {
        var attemptedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));
            using var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(payload)
            };
            if (!string.IsNullOrWhiteSpace(authorization))
            {
                request.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization);
            }

            using var response = await client.SendAsync(request, timeout.Token);
            return Record(
                channel,
                url,
                response.IsSuccessStatusCode,
                response.IsSuccessStatusCode
                    ? "Incident alert delivered."
                    : $"Incident alert failed with status {(int)response.StatusCode}.",
                attemptedAtUtc);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Record(channel, url, false, ex.Message, attemptedAtUtc);
        }
    }

    private static IntakeDeliveryRecord Record(
        string channel,
        string target,
        bool succeeded,
        string detail,
        DateTimeOffset attemptedAtUtc) => new(
        IntakeDeliveryTypes.IncidentAlert,
        channel,
        "distributed",
        "distributed_attack",
        target,
        succeeded ? IntakeDeliveryStatuses.Succeeded : IntakeDeliveryStatuses.Failed,
        detail,
        attemptedAtUtc);

    private static string BuildMessage(OperatorIncidentAlert alert)
    {
        var signal = alert.Signal;
        var originStatus = alert.Origins.Configured
            ? $"Primary {alert.Origins.PrimaryHost}: {(alert.Origins.PrimaryHealthy ? "healthy" : "unhealthy")}; " +
              $"backup {alert.Origins.BackupHost}: {(alert.Origins.BackupHealthy ? "healthy" : "unhealthy")}."
            : "Origin health checks are not configured.";
        return $"AI Scraping Defense detected a distributed attack: {signal.BlockedCount} of " +
               $"{signal.DecisionCount} decisions blocked across {signal.DistinctBlockedOrigins} origins " +
               $"since {signal.WindowStartUtc:O}. {originStatus}\n" +
               "Operator choices:\n- " + string.Join("\n- ", alert.OperatorChoices);
    }
}
