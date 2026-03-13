using System.Net;
using System.Net.Http;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class ThreatIntelligenceProviderTests
{
    [Fact]
    public async Task ConfiguredRangeProvider_ReturnsAssessmentForMatchingCidr()
    {
        var options = Options.Create(new DefenseEngineOptions
        {
            Escalation = new EscalationOptions
            {
                ConfiguredRanges = new ConfiguredRangeReputationOptions
                {
                    Enabled = true,
                    Entries =
                    [
                        new ReputationRangeEntry
                        {
                            Name = "test-range",
                            Cidr = "198.51.100.0/24",
                            ScoreAdjustment = 25
                        }
                    ]
                }
            }
        });
        var provider = new ConfiguredRangeReputationProvider(options);

        var assessment = await provider.AssessAsync(CreateContext("198.51.100.44"), CancellationToken.None);

        Assert.NotNull(assessment);
        Assert.Equal(25, assessment!.ScoreAdjustment);
        Assert.True(assessment.IsMalicious);
        Assert.Contains("reputation_range:test-range", assessment.Signals);
    }

    [Fact]
    public async Task HttpReputationProvider_ParsesMaliciousResponse()
    {
        var provider = new HttpReputationProvider(
            new FakeHttpClientFactory(
                CreateResponse("""
                {
                  "is_malicious": true,
                  "score_adjustment": 30,
                  "signals": ["reputation:http_feed"],
                  "summary": "Known scraper address."
                }
                """)),
            Options.Create(new DefenseEngineOptions
            {
                Escalation = new EscalationOptions
                {
                    HttpReputation = new HttpReputationProviderOptions
                    {
                        Enabled = true,
                        Endpoint = "https://reputation.example.test/check"
                    }
                }
            }),
            NullLogger<HttpReputationProvider>.Instance);

        var assessment = await provider.AssessAsync(CreateContext("198.51.100.45"), CancellationToken.None);

        Assert.NotNull(assessment);
        Assert.True(assessment!.IsMalicious);
        Assert.Equal(30, assessment.ScoreAdjustment);
        Assert.Contains("reputation:http_feed", assessment.Signals);
    }

    [Fact]
    public async Task OpenAiCompatibleModelAdapter_ParsesJsonClassificationResponse()
    {
        var adapter = new OpenAiCompatibleModelAdapter(
            new FakeHttpClientFactory(
                CreateResponse("""
                {
                  "choices": [
                    {
                      "message": {
                        "content": "{\"classification\":\"MALICIOUS_BOT\",\"summary\":\"Pattern matches scraper automation.\"}"
                      }
                    }
                  ]
                }
                """)),
            Options.Create(new DefenseEngineOptions
            {
                Escalation = new EscalationOptions
                {
                    OpenAiCompatibleModel = new OpenAiCompatibleModelAdapterOptions
                    {
                        Enabled = true,
                        Endpoint = "https://llm.example.test/v1/chat/completions",
                        Model = "test-model"
                    }
                }
            }),
            NullLogger<OpenAiCompatibleModelAdapter>.Instance);

        var assessment = await adapter.AssessAsync(CreateContext("198.51.100.46"), CancellationToken.None);

        Assert.NotNull(assessment);
        Assert.Equal("MALICIOUS_BOT", assessment!.Classification);
        Assert.True(assessment.IsBot);
        Assert.Equal(40, assessment.ScoreAdjustment);
        Assert.Contains("model_verdict:malicious_bot", assessment.Signals);
    }

    private static ThreatAssessmentContext CreateContext(string ipAddress)
    {
        return new ThreatAssessmentContext(
            ipAddress,
            "GET",
            "/probe",
            string.Empty,
            "test-agent",
            ["missing_accept_language"],
            2,
            15,
            10);
    }

    private static HttpResponseMessage CreateResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public FakeHttpClientFactory(HttpResponseMessage response)
        {
            _handler = new StaticResponseHandler(response);
        }

        public HttpClient CreateClient(string name)
        {
            return new HttpClient(_handler, disposeHandler: false);
        }
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public StaticResponseHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_response);
        }
    }
}
