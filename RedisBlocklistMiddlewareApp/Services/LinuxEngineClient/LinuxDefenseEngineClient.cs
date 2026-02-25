using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services.LinuxEngineClient;

public class LinuxDefenseEngineClient : IDefenseEngineClient
{
    private readonly HttpClient _httpClient;
    private readonly DefenseEngineOptions _options;
    private readonly ILogger<LinuxDefenseEngineClient> _logger;

    public LinuxDefenseEngineClient(
        HttpClient httpClient,
        IOptions<DefenseEngineOptions> options,
        ILogger<LinuxDefenseEngineClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;

        _httpClient.BaseAddress = new Uri(_options.EngineEndpoint);
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds));

        if (!string.IsNullOrWhiteSpace(_options.BearerToken))
        {
            _httpClient.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", _options.BearerToken);
        }

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("X-API-Key", _options.ApiKey);
        }
    }

    public Task<EngineHealthResponse> GetHealthAsync(CancellationToken cancellationToken = default) =>
        SendWithRetryAsync(
            () => _httpClient.GetFromJsonAsync<EngineHealthResponse>("/health", cancellationToken),
            fallback: () => Task.FromResult(new EngineHealthResponse("unreachable", DateTimeOffset.UtcNow, _options.EngineEndpoint)),
            operationName: "health-check");

    public Task<TelemetrySnapshotResponse> GetTelemetryAsync(CancellationToken cancellationToken = default) =>
        SendWithRetryAsync(
            () => _httpClient.GetFromJsonAsync<TelemetrySnapshotResponse>("/api/v1/telemetry", cancellationToken),
            fallback: () => Task.FromResult(new TelemetrySnapshotResponse(DateTimeOffset.UtcNow, 0, Array.Empty<TelemetryEvent>())),
            operationName: "telemetry-pull");

    public async Task<PolicySubmissionResponse> SubmitPolicyAsync(PolicySubmissionRequest request, CancellationToken cancellationToken = default)
    {
        return await SendWithRetryAsync(async () =>
        {
            var response = await _httpClient.PostAsJsonAsync("/api/v1/policies", request, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<PolicySubmissionResponse>(cancellationToken: cancellationToken)
                   ?? new PolicySubmissionResponse("unknown", "accepted", DateTimeOffset.UtcNow);
        },
        fallback: () => Task.FromResult(new PolicySubmissionResponse("pending", "queued-local", DateTimeOffset.UtcNow)),
        operationName: "policy-submit");
    }

    public async Task<EscalationAcknowledgementResponse> AcknowledgeEscalationAsync(EscalationAcknowledgementRequest request, CancellationToken cancellationToken = default)
    {
        return await SendWithRetryAsync(async () =>
        {
            var response = await _httpClient.PostAsJsonAsync("/api/v1/escalations/ack", request, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<EscalationAcknowledgementResponse>(cancellationToken: cancellationToken)
                   ?? new EscalationAcknowledgementResponse(request.EscalationId, "acknowledged", DateTimeOffset.UtcNow);
        },
        fallback: () => Task.FromResult(new EscalationAcknowledgementResponse(request.EscalationId, "engine-unavailable", DateTimeOffset.UtcNow)),
        operationName: "escalation-ack");
    }

    private async Task<T> SendWithRetryAsync<T>(Func<Task<T?>> action, Func<Task<T>> fallback, string operationName)
    {
        var maxAttempts = Math.Max(1, _options.RetryPolicy.MaxAttempts);
        var baseDelayMs = Math.Max(50, _options.RetryPolicy.BaseDelayMilliseconds);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var value = await action();
                if (value is not null)
                {
                    return value;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException && attempt < maxAttempts)
            {
                var delay = TimeSpan.FromMilliseconds(baseDelayMs * attempt);
                _logger.LogWarning(ex,
                    "Linux engine call failed for {Operation} on attempt {Attempt}/{MaxAttempts}; retrying in {DelayMs}ms.",
                    operationName,
                    attempt,
                    maxAttempts,
                    delay.TotalMilliseconds);
                await Task.Delay(delay);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex,
                    "Linux engine call failed for {Operation} after {MaxAttempts} attempts. Using fallback behavior.",
                    operationName,
                    maxAttempts);
                break;
            }
        }

        return await fallback();
    }
}
