using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class ThreatAssessmentServiceTests
{
    [Fact]
    public async Task AssessAsync_AggregatesProviderAndModelContributions()
    {
        var service = CreateService(
            frequency: 3,
            reputationProviders:
            [
                new TestReputationProvider(new ReputationAssessment(
                    "configured_ranges",
                    20,
                    false,
                    ["reputation_range:test_range"],
                    "Matched configured test range."))
            ],
            modelAdapters:
            [
                new TestModelAdapter(new ModelAssessment(
                    "openai_compatible_model",
                    -5,
                    false,
                    "BENIGN_CRAWLER",
                    ["model_verdict:benign_crawler"],
                    "Model considered the request a benign crawler."))
            ]);

        var result = await service.AssessAsync(new SuspiciousRequest(
            "198.51.100.10",
            "GET",
            "/products",
            string.Empty,
            "crawler",
            ["missing_accept_language"],
            DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.False(result.ShouldBlock);
        Assert.Equal(45, result.Score);
        Assert.Equal(3, result.Frequency);
        Assert.Equal(15, result.Breakdown.BaseSignalScore);
        Assert.Equal(15, result.Breakdown.FrequencyScore);
        Assert.Contains(result.Breakdown.Contributions, contribution => contribution.Source == "configured_ranges");
        Assert.Contains(result.Breakdown.Contributions, contribution => contribution.Source == "openai_compatible_model");
        Assert.Contains("reputation_range:test_range", result.Signals);
        Assert.Contains("model_verdict:benign_crawler", result.Signals);
    }

    [Fact]
    public async Task AssessAsync_BlocksWhenExplicitMaliciousVerdictIsReturned()
    {
        var service = CreateService(
            frequency: 1,
            reputationProviders: [],
            modelAdapters:
            [
                new TestModelAdapter(new ModelAssessment(
                    "openai_compatible_model",
                    5,
                    true,
                    "MALICIOUS_BOT",
                    ["model_verdict:malicious_bot"],
                    "Model classified the request as malicious."))
            ],
            configure: options =>
            {
                options.Heuristics.BlockScoreThreshold = 200;
                options.Heuristics.FrequencyBlockThreshold = 50;
            });

        var result = await service.AssessAsync(new SuspiciousRequest(
            "198.51.100.20",
            "GET",
            "/probe",
            string.Empty,
            "crawler",
            ["generic_accept_any"],
            DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.True(result.ShouldBlock);
        Assert.Equal("threat_intelligence_verdict", result.BlockReason);
        Assert.True(result.Breakdown.ExplicitMaliciousVerdict);
        Assert.Contains("model_verdict:malicious_bot", result.Signals);
    }

    [Fact]
    public async Task AssessAsync_BlocksWhenFrequencyThresholdIsReached()
    {
        var service = CreateService(
            frequency: 8,
            reputationProviders: [],
            modelAdapters: [],
            configure: options =>
            {
                options.Heuristics.BlockScoreThreshold = 500;
                options.Heuristics.FrequencyBlockThreshold = 8;
            });

        var result = await service.AssessAsync(new SuspiciousRequest(
            "198.51.100.30",
            "GET",
            "/probe",
            string.Empty,
            "crawler",
            ["long_query_string"],
            DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.True(result.ShouldBlock);
        Assert.Equal("frequency_threshold", result.BlockReason);
        Assert.Equal(35, result.Score);
    }

    private static ThreatAssessmentService CreateService(
        long frequency,
        IEnumerable<IThreatReputationProvider> reputationProviders,
        IEnumerable<IThreatModelAdapter> modelAdapters,
        Action<DefenseEngineOptions>? configure = null)
    {
        var options = new DefenseEngineOptions();
        configure?.Invoke(options);

        return new ThreatAssessmentService(
            new TestRequestFrequencyTracker(frequency),
            reputationProviders,
            modelAdapters,
            Options.Create(options),
            NullLogger<ThreatAssessmentService>.Instance);
    }

    private sealed class TestRequestFrequencyTracker : IRequestFrequencyTracker
    {
        private readonly long _frequency;

        public TestRequestFrequencyTracker(long frequency)
        {
            _frequency = frequency;
        }

        public Task<long> IncrementAsync(string ipAddress, CancellationToken cancellationToken)
        {
            return Task.FromResult(_frequency);
        }
    }

    private sealed class TestReputationProvider : IThreatReputationProvider
    {
        private readonly ReputationAssessment? _assessment;

        public TestReputationProvider(ReputationAssessment? assessment)
        {
            _assessment = assessment;
        }

        public string Name => "test_reputation";

        public Task<ReputationAssessment?> AssessAsync(ThreatAssessmentContext context, CancellationToken cancellationToken)
        {
            return Task.FromResult(_assessment);
        }
    }

    private sealed class TestModelAdapter : IThreatModelAdapter
    {
        private readonly ModelAssessment? _assessment;

        public TestModelAdapter(ModelAssessment? assessment)
        {
            _assessment = assessment;
        }

        public string Name => "test_model";

        public Task<ModelAssessment?> AssessAsync(ThreatAssessmentContext context, CancellationToken cancellationToken)
        {
            return Task.FromResult(_assessment);
        }
    }
}
