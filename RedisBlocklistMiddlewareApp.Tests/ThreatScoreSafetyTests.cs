using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class ThreatScoreSafetyTests
{
    [Theory]
    [InlineData(10, 5, 15)]
    [InlineData(10, -25, -15)]
    [InlineData(int.MaxValue, 1, ThreatScoreArithmetic.MaximumMagnitude)]
    [InlineData(int.MaxValue, int.MaxValue, ThreatScoreArithmetic.MaximumMagnitude)]
    [InlineData(int.MinValue, -1, -ThreatScoreArithmetic.MaximumMagnitude)]
    [InlineData(40, int.MaxValue, ThreatScoreArithmetic.MaximumMagnitude)]
    public void Add_SaturatesInsteadOfWrappingAround(int total, int delta, int expected)
    {
        Assert.Equal(expected, ThreatScoreArithmetic.Add(total, delta));
    }

    [Theory]
    [InlineData("""{"is_malicious": true, "score_adjustment": 30}""", 30)]
    [InlineData("""{"is_malicious": true, "score_adjustment": 50}""", 50)]
    [InlineData("""{"is_malicious": true, "score_adjustment": 51}""", 50)]
    [InlineData("""{"is_malicious": true, "score_adjustment": 2147483647}""", 50)]
    [InlineData("""{"is_malicious": true}""", 35)]
    // Too large for a 32-bit integer: not a usable number, so the configured default applies.
    [InlineData("""{"is_malicious": true, "score_adjustment": 99999999999}""", 35)]
    public async Task PositiveAdjustmentsAreBoundedByTheConfiguredMaximum(string body, int expected)
    {
        var assessment = await Assess(body);

        Assert.True(assessment!.IsMalicious, "the malicious verdict itself is unchanged");
        Assert.Equal(expected, assessment.ScoreAdjustment);
    }

    [Theory]
    [InlineData("""{"is_malicious": false, "score_adjustment": -1}""")]
    [InlineData("""{"is_malicious": false, "score_adjustment": -100}""")]
    [InlineData("""{"is_malicious": false, "score_adjustment": -2147483648}""")]
    public async Task NegativeAdjustmentsCannotCancelOtherEvidenceByDefault(string body)
    {
        Assert.Equal(0, (await Assess(body))!.ScoreAdjustment);
    }

    [Theory]
    [InlineData(-10, -10)]
    [InlineData(-50, -50)]
    [InlineData(-5000, -50)]
    [InlineData(int.MinValue, -50)]
    public async Task NegativeAdjustmentsAreAllowedAndBoundedWhenTheOperatorOptsIn(int sent, int expected)
    {
        var assessment = await Assess($$"""{"is_malicious": false, "score_adjustment": {{sent}}}""", allowNegative: true);

        Assert.Equal(expected, assessment!.ScoreAdjustment);
    }

    [Fact]
    public async Task AMaximumOfZeroNeutralisesTheProviderWithoutBreaking()
    {
        var assessment = await Assess("""{"is_malicious": true, "score_adjustment": 40}""", maximum: 0);

        Assert.True(assessment!.IsMalicious);
        Assert.Equal(0, assessment.ScoreAdjustment);
    }

    [Fact]
    public async Task OverflowingContributionsCannotEraseTheScoreOrTheVerdict()
    {
        // Two providers each report int.MaxValue. A plain int sum wraps to -2, which Math.Max(0, ...) floors to zero.
        var service = CreateService(
        [
            new FixedProvider("a", int.MaxValue, malicious: true),
            new FixedProvider("b", int.MaxValue, malicious: true)
        ]);

        var result = await service.AssessAsync(Request(), CancellationToken.None);

        Assert.True(result.ShouldBlock);
        Assert.Equal(ThreatScoreArithmetic.MaximumMagnitude, result.Score);
    }

    [Fact]
    public async Task AnEnormousNegativeContributionStillCannotUnderflowIntoAPositiveScore()
    {
        var service = CreateService(
        [
            new FixedProvider("a", int.MinValue, malicious: false),
            new FixedProvider("b", int.MinValue, malicious: false)
        ]);

        var result = await service.AssessAsync(Request(), CancellationToken.None);

        Assert.Equal(0, result.Score);
        Assert.False(result.ShouldBlock);
    }

    private static async Task<ReputationAssessment?> Assess(string body, bool allowNegative = false, int? maximum = null)
    {
        var http = new HttpReputationProviderOptions
        {
            Enabled = true,
            Endpoint = "https://reputation.example.test/check",
            AllowNegativeScoreAdjustments = allowNegative
        };
        if (maximum is not null)
        {
            http.MaxScoreAdjustment = maximum.Value;
        }

        var provider = new HttpReputationProvider(
            new FakeHttpClientFactory(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            }),
            Options.Create(new DefenseEngineOptions { Escalation = new EscalationOptions { HttpReputation = http } }),
            NullLogger<HttpReputationProvider>.Instance);

        return await provider.AssessAsync(
            new ThreatAssessmentContext("198.51.100.45", "GET", "/probe", string.Empty, "test-agent", ["missing_accept_language"], 2, 15, 10),
            CancellationToken.None);
    }

    private static SuspiciousRequest Request() =>
        new("198.51.100.60", "GET", "/probe", string.Empty, "crawler", ["missing_accept_language"], DateTimeOffset.UtcNow);

    private static ThreatAssessmentService CreateService(IEnumerable<IThreatReputationProvider> providers)
    {
        var options = new DefenseEngineOptions();
        var telemetry = TestTelemetryFactory.Create();
        return new ThreatAssessmentService(
            new FixedFrequencyTracker(),
            [new EdgeSignalScoreContributor(), new FrequencyScoreContributor()],
            providers,
            [],
            new ThreatModelRoutingStrategy(Options.Create(options)),
            new ContainmentPolicyEngine(
                [
                    new ExplicitVerdictContainmentContributor(),
                    new FrequencyContainmentContributor(Options.Create(options)),
                    new ThresholdBandContainmentContributor(Options.Create(options))
                ],
                telemetry,
                NullLogger<ContainmentPolicyEngine>.Instance),
            telemetry,
            NullLogger<ThreatAssessmentService>.Instance);
    }

    private sealed class FixedProvider : IThreatReputationProvider
    {
        private readonly ReputationAssessment _assessment;

        public FixedProvider(string name, int adjustment, bool malicious)
        {
            Name = name;
            _assessment = new ReputationAssessment(name, adjustment, malicious, [$"reputation:{name}"], "fixed");
        }

        public string Name { get; }

        public Task<ReputationAssessment?> AssessAsync(ThreatAssessmentContext context, CancellationToken cancellationToken) =>
            Task.FromResult<ReputationAssessment?>(_assessment);
    }

    private sealed class FixedFrequencyTracker : IRequestFrequencyTracker
    {
        public Task<long> IncrementAsync(string ipAddress, CancellationToken cancellationToken) => Task.FromResult(1L);
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public FakeHttpClientFactory(HttpResponseMessage response)
        {
            _handler = new StaticHandler(response);
        }

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    private sealed class StaticHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public StaticHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_response);
    }
}
