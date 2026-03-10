using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services.LinuxEngineClient;

public class LinuxDefenseEngineClient : IDefenseEngineClient
{
    public const string HttpClientName = "LinuxDefenseEngine";
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DefenseEngineOptions _options;
    private readonly DefenseEngineApiRoutesOptions _routes;
    private readonly ILogger<LinuxDefenseEngineClient> _logger;

    public LinuxDefenseEngineClient(
        IHttpClientFactory httpClientFactory,
        IOptions<DefenseEngineOptions> options,
        IOptions<DefenseEngineApiRoutesOptions> routes,
        ILogger<LinuxDefenseEngineClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _routes = routes.Value;
        _logger = logger;
    }

    public Task<EngineHealthResponse> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        return SendWithRetryAsync(
            ct => client.GetFromJsonAsync<EngineHealthResponse>(_routes.HealthPath, ct),
            fallback: () => Task.FromResult(new EngineHealthResponse("unreachable", DateTimeOffset.UtcNow, _options.EngineEndpoint)),
            operationName: "health-check",
            cancellationToken);
    }

    public Task<TelemetrySnapshotResponse> GetTelemetryAsync(CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        return SendWithRetryAsync(
            ct => client.GetFromJsonAsync<TelemetrySnapshotResponse>(_routes.TelemetryPath, ct),
            fallback: () => Task.FromResult(new TelemetrySnapshotResponse(DateTimeOffset.UtcNow, 0, Array.Empty<TelemetryEvent>())),
            operationName: "telemetry-pull",
            cancellationToken);
    }

    public async Task<PolicySubmissionResponse> SubmitPolicyAsync(PolicySubmissionRequest request, CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        return await SendWithRetryAsync(async ct =>
        {
            var response = await client.PostAsJsonAsync(_routes.PoliciesPath, request, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<PolicySubmissionResponse>(cancellationToken: ct)
                   ?? new PolicySubmissionResponse("unknown", "accepted", DateTimeOffset.UtcNow);
        },
        fallback: () => Task.FromResult(new PolicySubmissionResponse("pending", "queued-local", DateTimeOffset.UtcNow)),
        operationName: "policy-submit",
        cancellationToken);
    }

    public async Task<EscalationAcknowledgementResponse> AcknowledgeEscalationAsync(EscalationAcknowledgementRequest request, CancellationToken cancellationToken = default)
    {
        var client = _httpClientFactory.CreateClient(HttpClientName);
        return await SendWithRetryAsync(async ct =>
        {
            var response = await client.PostAsJsonAsync(_routes.EscalationAckPath, request, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<EscalationAcknowledgementResponse>(cancellationToken: ct)
                   ?? new EscalationAcknowledgementResponse(request.EscalationId, "acknowledged", DateTimeOffset.UtcNow);
        },
        fallback: () => Task.FromResult(new EscalationAcknowledgementResponse(request.EscalationId, "engine-unavailable", DateTimeOffset.UtcNow)),
        operationName: "escalation-ack",
        cancellationToken);
    }

    private async Task<T> SendWithRetryAsync<T>(
        Func<CancellationToken, Task<T?>> action,
        Func<Task<T>> fallback,
        string operationName,
        CancellationToken cancellationToken = default)
    {
        var maxAttempts = Math.Max(1, _options.RetryPolicy.MaxAttempts);
        var baseDelayMs = Math.Max(50, _options.RetryPolicy.BaseDelayMilliseconds);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var value = await action(cancellationToken);
                if (value is not null)
                {
                    return value;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxAttempts)
            {
                var delay = TimeSpan.FromMilliseconds(baseDelayMs * attempt);
                _logger.LogWarning(ex,
                    "Linux engine call failed for {Operation} on attempt {Attempt}/{MaxAttempts}; retrying in {DelayMs}ms.",
                    operationName,
                    attempt,
                    maxAttempts,
                    delay.TotalMilliseconds);
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex)
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
