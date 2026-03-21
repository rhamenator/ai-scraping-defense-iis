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
        var cancellationToken = TestContext.Current.CancellationToken;
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
            var response = await client.SendAsync(suspiciousRequest, cancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        var metricsBody = await WaitForMetricsAsync(client, body =>
            body.Contains("ai_scraping_defense_suspicious_requests_total", StringComparison.Ordinal) &&
            body.Contains("ai_scraping_defense_queue_depth", StringComparison.Ordinal) &&
            body.Contains("ai_scraping_defense_queue_capacity", StringComparison.Ordinal), cancellationToken);

        Assert.NotNull(metricsBody);
    }

    private static async Task<string?> WaitForMetricsAsync(HttpClient client, Func<string, bool> predicate, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync("/metrics", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (predicate(body))
                {
                    return body;
                }
            }

            await Task.Delay(200, cancellationToken);
        }

        return null;
    }
}
