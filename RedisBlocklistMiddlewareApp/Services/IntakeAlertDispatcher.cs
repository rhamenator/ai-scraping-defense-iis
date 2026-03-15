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
        var results = new List<IntakeDeliveryRecord>(3);
        var webhookResult = await SendGenericWebhookAsync(webhookEvent, cancellationToken);
        if (webhookResult is not null)
        {
            results.Add(webhookResult);
        }

        var slackResult = await SendSlackAsync(webhookEvent, cancellationToken);
        if (slackResult is not null)
        {
            results.Add(slackResult);
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

    private async Task<IntakeDeliveryRecord?> SendSlackAsync(
        IntakeWebhookEvent webhookEvent,
        CancellationToken cancellationToken)
    {
        var options = _options.Slack;
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.WebhookUrl))
        {
            return null;
        }

        var attemptedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds));

            using var request = new HttpRequestMessage(HttpMethod.Post, options.WebhookUrl)
            {
                Content = JsonContent.Create(BuildSlackPayload(webhookEvent))
            };

            using var response = await client.SendAsync(request, cancellationToken);
            var detail = response.IsSuccessStatusCode
                ? "Alert delivered successfully."
                : $"Alert delivery failed with status {(int)response.StatusCode}.";

            return new IntakeDeliveryRecord(
                IntakeDeliveryTypes.Alert,
                IntakeDeliveryChannels.Slack,
                webhookEvent.Details.IpAddress,
                webhookEvent.Reason,
                options.WebhookUrl,
                response.IsSuccessStatusCode ? IntakeDeliveryStatuses.Succeeded : IntakeDeliveryStatuses.Failed,
                detail,
                attemptedAtUtc);
        }
        catch (Exception ex)
        {
            return new IntakeDeliveryRecord(
                IntakeDeliveryTypes.Alert,
                IntakeDeliveryChannels.Slack,
                webhookEvent.Details.IpAddress,
                webhookEvent.Reason,
                options.WebhookUrl,
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

    private static object BuildSlackPayload(IntakeWebhookEvent webhookEvent)
    {
        var detail = webhookEvent.Details;
        var userAgent = string.IsNullOrWhiteSpace(detail.UserAgent) ? "N/A" : detail.UserAgent;
        var method = string.IsNullOrWhiteSpace(detail.Method) ? "N/A" : detail.Method;
        var path = string.IsNullOrWhiteSpace(detail.Path) ? "/" : detail.Path;
        var queryString = string.IsNullOrWhiteSpace(detail.QueryString) ? string.Empty : detail.QueryString;
        var signals = detail.Signals is { Count: > 0 } ? string.Join(", ", detail.Signals) : "None";
        var pathDisplay = string.IsNullOrEmpty(queryString) ? path : $"{path}{queryString}";
        var message = $":shield: *AI Defense Alert*\n" +
                      $"> *Reason:* {webhookEvent.Reason}\n" +
                      $"> *IP Address:* `{detail.IpAddress}`\n" +
                      $"> *Method:* `{method}`\n" +
                      $"> *Path:* `{pathDisplay}`\n" +
                      $"> *User Agent:* `{userAgent}`\n" +
                      $"> *Signals:* {signals}\n" +
                      $"> *Timestamp (UTC):* {webhookEvent.TimestampUtc:O}";

        return new
        {
            text = message,
            blocks = new object[]
            {
                new
                {
                    type = "section",
                    text = new
                    {
                        type = "mrkdwn",
                        text = "*AI Defense Alert*\nConfirmed malicious traffic was processed by the intake pipeline."
                    }
                },
                new
                {
                    type = "section",
                    fields = new object[]
                    {
                        new { type = "mrkdwn", text = $"*Reason:*\n{EscapeSlack(webhookEvent.Reason)}" },
                        new { type = "mrkdwn", text = $"*IP Address:*\n`{EscapeSlack(detail.IpAddress)}`" },
                        new { type = "mrkdwn", text = $"*Method:*\n`{EscapeSlack(method)}`" },
                        new { type = "mrkdwn", text = $"*Path:*\n`{EscapeSlack(pathDisplay)}`" },
                        new { type = "mrkdwn", text = $"*User Agent:*\n`{EscapeSlack(userAgent)}`" },
                        new { type = "mrkdwn", text = $"*Timestamp (UTC):*\n{webhookEvent.TimestampUtc:O}" }
                    }
                },
                new
                {
                    type = "section",
                    text = new
                    {
                        type = "mrkdwn",
                        text = $"*Signals:*\n{EscapeSlack(signals)}"
                    }
                }
            }
        };
    }

    private static string EscapeSlack(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal);
    }
}
