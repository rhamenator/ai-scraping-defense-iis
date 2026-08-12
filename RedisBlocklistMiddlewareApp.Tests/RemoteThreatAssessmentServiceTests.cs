using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class RemoteThreatAssessmentServiceTests
{
    [Fact]
    public async Task AssessAsync_RetriesTransientFailuresAndAuthenticatesRequest()
    {
        var expected = CreateResult();
        var handler = new SequenceHandler((attempt, request) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(new string('s', 32), request.Headers.Authorization?.Parameter);
            return attempt < 3
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(expected, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                        Encoding.UTF8,
                        "application/json")
                };
        });
        var service = CreateService(handler);

        var result = await service.AssessAsync(CreateRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(3, handler.Attempts);
        Assert.Equal(expected.Action, result.Action);
        Assert.Equal(expected.Score, result.Score);
    }

    [Fact]
    public async Task AssessAsync_FailsVisiblyAfterBoundedRetries()
    {
        var handler = new SequenceHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.BadGateway));
        var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.AssessAsync(CreateRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(3, handler.Attempts);
        Assert.Contains("three attempts", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static RemoteThreatAssessmentService CreateService(HttpMessageHandler handler)
    {
        var options = Options.Create(new DefenseEngineOptions
        {
            Topology = new TopologyOptions
            {
                Mode = RuntimeTopologyModes.Split,
                EscalationBaseUrl = "http://escalation.test",
                ServiceToken = new string('s', 32),
                RequestTimeoutSeconds = 2
            }
        });
        return new RemoteThreatAssessmentService(new HttpClient(handler), options);
    }

    private static SuspiciousRequest CreateRequest() =>
        new(
            "198.51.100.20",
            "GET",
            "/products",
            string.Empty,
            "test-agent",
            ["signal"],
            DateTimeOffset.UtcNow);

    private static ThreatAssessmentResult CreateResult() =>
        new(
            ContainmentActions.Observed,
            false,
            "score_thresholds",
            "Observed remotely.",
            10,
            1,
            ["signal"],
            new DefenseScoreBreakdown(10, 0, 10, false, []));

    private sealed class SequenceHandler(
        Func<int, HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(responder(Attempts, request));
        }
    }
}
