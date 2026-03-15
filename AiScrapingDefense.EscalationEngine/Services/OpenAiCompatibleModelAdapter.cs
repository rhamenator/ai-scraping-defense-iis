using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class OpenAiCompatibleModelAdapter : IThreatModelAdapter
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly OpenAiCompatibleModelAdapterOptions _options;
    private readonly ILogger<OpenAiCompatibleModelAdapter> _logger;

    public OpenAiCompatibleModelAdapter(
        IHttpClientFactory httpClientFactory,
        IOptions<DefenseEngineOptions> options,
        ILogger<OpenAiCompatibleModelAdapter> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value.Escalation.OpenAiCompatibleModel;
        _logger = logger;
    }

    public string Name => "openai_compatible_model";

    public async Task<ModelAssessment?> AssessAsync(
        ThreatAssessmentContext context,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled ||
            string.IsNullOrWhiteSpace(_options.Endpoint) ||
            string.IsNullOrWhiteSpace(_options.Model))
        {
            return null;
        }

        try
        {
            var serializedContext = JsonSerializer.Serialize(new
            {
                ip = context.IpAddress,
                method = context.Method,
                path = context.Path,
                query_string = context.QueryString,
                user_agent = context.UserAgent,
                signals = context.Signals,
                frequency = context.Frequency,
                base_signal_score = context.BaseSignalScore,
                frequency_score = context.FrequencyScore
            });

            using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint)
            {
                Content = JsonContent.Create(new
                {
                    model = _options.Model,
                    temperature = 0.1,
                    response_format = new { type = "json_object" },
                    messages = new object[]
                    {
                        new
                        {
                            role = "system",
                            content = _options.SystemPrompt
                        },
                        new
                        {
                            role = "user",
                            content =
                                "Classify this request and return JSON with classification and summary only: " +
                                serializedContext
                        }
                    }
                })
            };

            if (!string.IsNullOrWhiteSpace(_options.ApiKey))
            {
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer",
                    _options.ApiKey);
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));

            var client = _httpClientFactory.CreateClient(Name);
            using var response = await client.SendAsync(request, timeoutCts.Token);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "OpenAI-compatible model adapter returned status code {StatusCode}.",
                    (int)response.StatusCode);
                return null;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
            using var envelope = await JsonDocument.ParseAsync(responseStream, cancellationToken: timeoutCts.Token);

            if (!TryGetMessageContent(envelope.RootElement, out var content))
            {
                _logger.LogWarning("OpenAI-compatible model adapter returned an unexpected response shape.");
                return null;
            }

            var classification = ExtractClassification(content);
            var summary = ExtractSummary(content) ??
                "OpenAI-compatible model adapter returned a classification verdict.";

            return classification switch
            {
                "MALICIOUS_BOT" => new ModelAssessment(
                    Name,
                    _options.MaliciousScoreAdjustment,
                    true,
                    classification,
                    ["model_verdict:malicious_bot"],
                    summary),
                "BENIGN_CRAWLER" => new ModelAssessment(
                    Name,
                    _options.BenignCrawlerScoreAdjustment,
                    false,
                    classification,
                    ["model_verdict:benign_crawler"],
                    summary),
                "HUMAN" => new ModelAssessment(
                    Name,
                    _options.HumanScoreAdjustment,
                    false,
                    classification,
                    ["model_verdict:human"],
                    summary),
                _ => new ModelAssessment(
                    Name,
                    0,
                    null,
                    "INCONCLUSIVE",
                    [],
                    summary)
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("OpenAI-compatible model adapter timed out.");
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OpenAI-compatible model adapter failed.");
            return null;
        }
    }

    private static bool TryGetMessageContent(JsonElement root, out string content)
    {
        content = string.Empty;

        if (!root.TryGetProperty("choices", out var choicesElement) ||
            choicesElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var firstChoice = choicesElement.EnumerateArray().FirstOrDefault();
        if (firstChoice.ValueKind == JsonValueKind.Undefined ||
            !firstChoice.TryGetProperty("message", out var messageElement) ||
            !messageElement.TryGetProperty("content", out var contentElement) ||
            contentElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        content = contentElement.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(content);
    }

    private static string ExtractClassification(string content)
    {
        if (TryParseJsonContent(content, out var root) &&
            root.TryGetProperty("classification", out var classificationElement) &&
            classificationElement.ValueKind == JsonValueKind.String)
        {
            return NormalizeClassification(classificationElement.GetString());
        }

        return NormalizeClassification(content);
    }

    private static string? ExtractSummary(string content)
    {
        if (TryParseJsonContent(content, out var root) &&
            root.TryGetProperty("summary", out var summaryElement) &&
            summaryElement.ValueKind == JsonValueKind.String)
        {
            return summaryElement.GetString();
        }

        return null;
    }

    private static bool TryParseJsonContent(string content, out JsonElement root)
    {
        root = default;

        try
        {
            using var document = JsonDocument.Parse(content);
            root = document.RootElement.Clone();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeClassification(string? raw)
    {
        var normalized = raw?.Trim().ToUpperInvariant() ?? string.Empty;

        if (normalized.Contains("MALICIOUS_BOT", StringComparison.Ordinal))
        {
            return "MALICIOUS_BOT";
        }

        if (normalized.Contains("BENIGN_CRAWLER", StringComparison.Ordinal))
        {
            return "BENIGN_CRAWLER";
        }

        if (normalized.Contains("HUMAN", StringComparison.Ordinal))
        {
            return "HUMAN";
        }

        return "INCONCLUSIVE";
    }
}
