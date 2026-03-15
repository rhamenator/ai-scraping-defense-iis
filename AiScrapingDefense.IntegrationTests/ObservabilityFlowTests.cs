using System.Net;

namespace AiScrapingDefense.IntegrationTests;

[Collection(EndToEndCollection.Name)]
public sealed class ObservabilityFlowTests
{
    private readonly DefenseStackFixture _fixture;

    public ObservabilityFlowTests(DefenseStackFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task MetricsEndpoint_ExposesDefenseMetrics_WhenPrometheusIsEnabled()
    {
        await using var host = await _fixture.CreateHostAsync(new Dictionary<string, string?>
        {
            ["DefenseEngine:Observability:EnablePrometheusEndpoint"] = "true"
        });
        var client = host.Client;

        using (var suspiciousRequest = new HttpRequestMessage(HttpMethod.Get, "/docs"))
        {
            suspiciousRequest.Headers.Add(HeaderDrivenClientIpResolver.HeaderName, "198.51.100.71");
            suspiciousRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            suspiciousRequest.Headers.Accept.ParseAdd("*/*");
            var response = await client.SendAsync(suspiciousRequest);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var metricsBody = await WaitForMetricsAsync(client, body =>
            body.Contains("ai_scraping_defense_suspicious_requests_total", StringComparison.Ordinal) &&
            body.Contains("ai_scraping_defense_queue_depth", StringComparison.Ordinal) &&
            body.Contains("ai_scraping_defense_queue_capacity", StringComparison.Ordinal));

        Assert.NotNull(metricsBody);
    }

    private static async Task<string?> WaitForMetricsAsync(HttpClient client, Func<string, bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var response = await client.GetAsync("/metrics");
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync();
                if (predicate(body))
                {
                    return body;
                }
            }

            await Task.Delay(200);
        }

        return null;
    }
}
