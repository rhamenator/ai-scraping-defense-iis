using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class RemoteThreatAssessmentService : IThreatAssessmentService
{
    private readonly HttpClient _client;
    private readonly TopologyOptions _options;

    public RemoteThreatAssessmentService(
        HttpClient client,
        IOptions<DefenseEngineOptions> options)
    {
        _client = client;
        _options = options.Value.Topology;
        _client.BaseAddress = new Uri(_options.EscalationBaseUrl + "/", UriKind.Absolute);
        _client.Timeout = TimeSpan.FromSeconds(_options.RequestTimeoutSeconds);
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _options.ServiceToken);
    }

    public async Task<ThreatAssessmentResult> AssessAsync(
        SuspiciousRequest request,
        CancellationToken cancellationToken)
    {
        Exception? lastFailure = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var response = await _client.PostAsJsonAsync(
                    "v1/assess",
                    request,
                    cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return await response.Content.ReadFromJsonAsync<ThreatAssessmentResult>(cancellationToken)
                        ?? throw new InvalidOperationException("The escalation runtime returned an empty response.");
                }

                lastFailure = new HttpRequestException(
                    $"Escalation runtime returned HTTP {(int)response.StatusCode}.",
                    null,
                    response.StatusCode);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (attempt < 3)
            {
                lastFailure = exception;
            }

            if (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100d * attempt), cancellationToken);
            }
        }

        throw new HttpRequestException(
            "The escalation runtime remained unavailable after three attempts.",
            lastFailure);
    }
}
