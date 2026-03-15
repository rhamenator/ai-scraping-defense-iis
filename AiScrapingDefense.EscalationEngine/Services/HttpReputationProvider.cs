using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class HttpReputationProvider : IThreatReputationProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly HttpReputationProviderOptions _options;
    private readonly ILogger<HttpReputationProvider> _logger;

    public HttpReputationProvider(
        IHttpClientFactory httpClientFactory,
        IOptions<DefenseEngineOptions> options,
        ILogger<HttpReputationProvider> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value.Escalation.HttpReputation;
        _logger = logger;
    }

    public string Name => "http_reputation";

    public async Task<ReputationAssessment?> AssessAsync(
        ThreatAssessmentContext context,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.Endpoint))
        {
            return null;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
            {
                Content = JsonContent.Create(new
                {
                    ip = context.IpAddress,
                    path = context.Path,
                    signals = context.Signals,
                    frequency = context.Frequency
                })
            };

            if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                request.Headers.TryAddWithoutValidation(
                    _options.ApiKeyHeaderName,
                    _options.ApiKey);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));

            var client = _httpClientFactory.CreateClient(Name);
            using var response = await client.SendAsync(request, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "HTTP reputation provider returned status code {StatusCode}.",
                    (int)response.StatusCode);
                return null;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: timeoutCts.Token);
            var root = document.RootElement;

            var isMalicious = root.TryGetProperty("is_malicious", out var maliciousElement) &&
                maliciousElement.ValueKind is JsonValueKind.True or JsonValueKind.False &&
                maliciousElement.GetBoolean();
            var scoreAdjustment = root.TryGetProperty("score_adjustment", out var scoreElement) &&
                scoreElement.TryGetInt32(out var explicitScore)
                ? explicitScore
                : (isMalicious ? _options.MaliciousScoreAdjustment : 0);
            var summary = root.TryGetProperty("summary", out var summaryElement) &&
                summaryElement.ValueKind == JsonValueKind.String
                ? summaryElement.GetString() ?? string.Empty
                : string.Empty;
            var signals = root.TryGetProperty("signals", out var signalsElement) &&
                signalsElement.ValueKind == JsonValueKind.Array
                ? signalsElement.EnumerateArray()
                    .Where(element => element.ValueKind == JsonValueKind.String)
                    .Select(element => element.GetString()!)
                    .Where(signal => !string.IsNullOrWhiteSpace(signal))
                    .ToArray()
                : [];

            if (string.IsNullOrWhiteSpace(summary))
            {
                summary = isMalicious
                    ? "HTTP reputation provider flagged the IP as malicious."
                    : "HTTP reputation provider returned a neutral reputation verdict.";
            }

            if (signals.Length == 0 && isMalicious)
            {
                signals = ["reputation:http_malicious_ip"];
            }

            return new ReputationAssessment(
                Name,
                scoreAdjustment,
                isMalicious,
                signals,
                summary);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("HTTP reputation provider timed out.");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HTTP reputation provider failed.");
            return null;
        }
    }
}
