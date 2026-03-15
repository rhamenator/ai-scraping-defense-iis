using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class IntakeAlertDispatcher : IIntakeAlertDispatcher
{
    private readonly IntakeAlertingOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISmtpAlertSender _smtpAlertSender;

    public IntakeAlertDispatcher(
        IOptions<DefenseEngineOptions> options,
        IHttpClientFactory httpClientFactory,
        ISmtpAlertSender smtpAlertSender)
    {
        _options = options.Value.Intake.Alerting;
        _httpClientFactory = httpClientFactory;
        _smtpAlertSender = smtpAlertSender;
    }

    public async Task<IReadOnlyList<IntakeDeliveryRecord>> DispatchAsync(
        IntakeWebhookEvent webhookEvent,
        CancellationToken cancellationToken)
    {
        var results = new List<IntakeDeliveryRecord>(2);
        var webhookResult = await SendGenericWebhookAsync(webhookEvent, cancellationToken);
        if (webhookResult is not null)
        {
            results.Add(webhookResult);
        }

        var smtpResult = await SendSmtpAsync(webhookEvent, cancellationToken);
        if (smtpResult is not null)
        {
            results.Add(smtpResult);
        }

        return results;
    }

    private async Task<IntakeDeliveryRecord?> SendGenericWebhookAsync(
        IntakeWebhookEvent webhookEvent,
        CancellationToken cancellationToken)
    {
        var options = _options.GenericWebhook;
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.Url))
        {
            return null;
        }

        var attemptedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds));

            using var request = new HttpRequestMessage(HttpMethod.Post, options.Url)
            {
                Content = JsonContent.Create(new
                {
                    alert_type = "AI_DEFENSE_BLOCK",
                    event_type = webhookEvent.EventType,
                    webhookEvent.Reason,
                    timestamp_utc = webhookEvent.TimestampUtc,
                    ip_address = webhookEvent.Details.IpAddress,
                    webhookEvent.Details
                })
            };

            if (!string.IsNullOrWhiteSpace(options.AuthorizationHeaderValue))
            {
                request.Headers.Authorization = AuthenticationHeaderValue.Parse(options.AuthorizationHeaderValue);
            }

            using var response = await client.SendAsync(request, cancellationToken);
            var detail = response.IsSuccessStatusCode
                ? "Alert delivered successfully."
                : $"Alert delivery failed with status {(int)response.StatusCode}.";

            return new IntakeDeliveryRecord(
                IntakeDeliveryTypes.Alert,
                IntakeDeliveryChannels.GenericWebhook,
                webhookEvent.Details.IpAddress,
                webhookEvent.Reason,
                options.Url,
                response.IsSuccessStatusCode ? IntakeDeliveryStatuses.Succeeded : IntakeDeliveryStatuses.Failed,
                detail,
                attemptedAtUtc);
        }
        catch (Exception ex)
        {
            return new IntakeDeliveryRecord(
                IntakeDeliveryTypes.Alert,
                IntakeDeliveryChannels.GenericWebhook,
                webhookEvent.Details.IpAddress,
                webhookEvent.Reason,
                options.Url,
                IntakeDeliveryStatuses.Failed,
                ex.Message,
                attemptedAtUtc);
        }
    }

    private async Task<IntakeDeliveryRecord?> SendSmtpAsync(
        IntakeWebhookEvent webhookEvent,
        CancellationToken cancellationToken)
    {
        var options = _options.Smtp;
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.Host) || string.IsNullOrWhiteSpace(options.From) || options.To.Length == 0)
        {
            return null;
        }

        var attemptedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            var subject = $"[AI Defense Alert] {webhookEvent.Reason}";
            var body = BuildSmtpBody(webhookEvent);
            await _smtpAlertSender.SendAsync(
                options.Host,
                options.Port,
                options.Username,
                options.Password,
                options.UseTls,
                options.From,
                options.To,
                subject,
                body,
                cancellationToken);

            return new IntakeDeliveryRecord(
                IntakeDeliveryTypes.Alert,
                IntakeDeliveryChannels.Smtp,
                webhookEvent.Details.IpAddress,
                webhookEvent.Reason,
                string.Join(",", options.To),
                IntakeDeliveryStatuses.Succeeded,
                "Alert delivered successfully.",
                attemptedAtUtc);
        }
        catch (Exception ex)
        {
            return new IntakeDeliveryRecord(
                IntakeDeliveryTypes.Alert,
                IntakeDeliveryChannels.Smtp,
                webhookEvent.Details.IpAddress,
                webhookEvent.Reason,
                string.Join(",", options.To),
                IntakeDeliveryStatuses.Failed,
                ex.Message,
                attemptedAtUtc);
        }
    }

    private static string BuildSmtpBody(IntakeWebhookEvent webhookEvent)
    {
        var detail = webhookEvent.Details;
        var builder = new StringBuilder();
        builder.AppendLine("Confirmed malicious traffic was processed by the intake pipeline.");
        builder.AppendLine();
        builder.AppendLine($"Reason: {webhookEvent.Reason}");
        builder.AppendLine($"Timestamp (UTC): {webhookEvent.TimestampUtc:O}");
        builder.AppendLine($"IP Address: {detail.IpAddress}");
        builder.AppendLine($"Method: {detail.Method ?? "N/A"}");
        builder.AppendLine($"Path: {detail.Path ?? "/"}");
        builder.AppendLine($"User Agent: {detail.UserAgent ?? "N/A"}");
        builder.AppendLine($"Signals: {string.Join(", ", detail.Signals ?? [])}");
        return builder.ToString();
    }
}

