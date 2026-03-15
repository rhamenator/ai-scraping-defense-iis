using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class CommunityReporter : ICommunityReporter
{
    private readonly CommunityReportingOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;

    public CommunityReporter(
        IOptions<DefenseEngineOptions> options,
        IHttpClientFactory httpClientFactory)
    {
        _options = options.Value.Intake.CommunityReporting;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<IntakeDeliveryRecord?> ReportAsync(
        IntakeWebhookEvent webhookEvent,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled ||
            string.IsNullOrWhiteSpace(_options.Endpoint) ||
            string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return null;
        }

        var attemptedAtUtc = DateTimeOffset.UtcNow;
        try
        {
            using var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds));

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["ip"] = webhookEvent.Details.IpAddress,
                    ["categories"] = SelectCategories(webhookEvent),
                    ["comment"] = BuildComment(webhookEvent)
                })
            };
            request.Headers.TryAddWithoutValidation(_options.ApiKeyHeaderName, _options.ApiKey);

            using var response = await client.SendAsync(request, cancellationToken);
            var detail = response.IsSuccessStatusCode
                ? "Community report delivered successfully."
                : $"Community report failed with status {(int)response.StatusCode}.";

            return new IntakeDeliveryRecord(
                IntakeDeliveryTypes.CommunityReport,
                _options.ProviderName,
                webhookEvent.Details.IpAddress,
                webhookEvent.Reason,
                _options.Endpoint,
                response.IsSuccessStatusCode ? IntakeDeliveryStatuses.Succeeded : IntakeDeliveryStatuses.Failed,
                detail,
                attemptedAtUtc);
        }
        catch (Exception ex)
        {
            return new IntakeDeliveryRecord(
                IntakeDeliveryTypes.CommunityReport,
                _options.ProviderName,
                webhookEvent.Details.IpAddress,
                webhookEvent.Reason,
                _options.Endpoint,
                IntakeDeliveryStatuses.Failed,
                ex.Message,
                attemptedAtUtc);
        }
    }

    private string SelectCategories(IntakeWebhookEvent webhookEvent)
    {
        var reason = webhookEvent.Reason;
        if (reason.Contains("scan", StringComparison.OrdinalIgnoreCase))
        {
            return "14";
        }

        if (reason.Contains("honeypot", StringComparison.OrdinalIgnoreCase))
        {
            return "22";
        }

        if (reason.Contains("scrap", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("crawler", StringComparison.OrdinalIgnoreCase) ||
            reason.Contains("bot", StringComparison.OrdinalIgnoreCase))
        {
            return "19";
        }

        return _options.DefaultCategories;
    }

    private string BuildComment(IntakeWebhookEvent webhookEvent)
    {
        var detail = webhookEvent.Details;
        return $"{_options.CommentPrefix}. Reason: {webhookEvent.Reason}. UA: {detail.UserAgent ?? "N/A"}. Path: {detail.Path ?? "/"}.";
    }
}

