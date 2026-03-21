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
            ],
            configure: options =>
            {
                options.Escalation.Containment.ChallengeScoreThreshold = 100;
                options.Escalation.Containment.TarpitScoreThreshold = 110;
                options.Escalation.Containment.ThrottleScoreThreshold = 120;
                options.Escalation.Containment.BlockScoreThreshold = 130;
                options.Escalation.Containment.FrequencyBlockThreshold = 20;
            });

        var result = await service.AssessAsync(new SuspiciousRequest(
            "198.51.100.10",
            "GET",
            "/products",
            string.Empty,
            "crawler",
            ["missing_accept_language"],
            DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.False(result.ShouldBlock);
        Assert.Equal(ContainmentActions.Observed, result.Action);
        Assert.Equal(45, result.Score);
        Assert.Equal(3, result.Frequency);
        Assert.Equal(15, result.Breakdown.BaseSignalScore);
        Assert.Equal(15, result.Breakdown.FrequencyScore);
        Assert.Contains(result.Breakdown.Contributions, contribution => contribution.Source == "model_routing");
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
        Assert.Equal(ContainmentActions.Blocked, result.Action);
        Assert.Equal("threat_intelligence_verdict", result.DecisionReason);
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
        Assert.Equal(ContainmentActions.Blocked, result.Action);
        Assert.Equal("frequency_threshold", result.DecisionReason);
        Assert.Equal(35, result.Score);
    }

    [Fact]
    public async Task AssessAsync_PrefersLocalRouteAndSkipsRemoteWhenLocalAdapterIsDecisive()
    {
        var localAdapter = new TestModelAdapter(
            route: ThreatModelRoutes.Local,
            assessment: new ModelAssessment(
                "local_trained_model",
                35,
                true,
                "MALICIOUS_BOT",
                ["local_model:malicious"],
                "Local model classified the request as malicious."));
        var remoteAdapter = new TestModelAdapter(
            route: ThreatModelRoutes.Remote,
            assessment: new ModelAssessment(
                "openai_compatible_model",
                -10,
                false,
                "HUMAN",
                ["model_verdict:human"],
                "Remote model classified the request as human."));

        var service = CreateService(
            frequency: 1,
            reputationProviders: [],
            modelAdapters: [localAdapter, remoteAdapter],
            configure: options =>
            {
                options.Escalation.Routing.PreferredPrimaryRoute = ThreatModelRoutes.Local;
                options.Escalation.Routing.RemoteFallbackEnabled = true;
            });

        var result = await service.AssessAsync(new SuspiciousRequest(
            "198.51.100.31",
            "GET",
            "/probe",
            string.Empty,
            "crawler",
            ["generic_accept_any"],
            DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.True(result.ShouldBlock);
        Assert.Equal(1, localAdapter.CallCount);
        Assert.Equal(0, remoteAdapter.CallCount);
    }

    [Fact]
    public async Task AssessAsync_FallsBackToRemoteRouteWhenLocalAdapterIsInconclusive()
    {
        var localAdapter = new TestModelAdapter(
            route: ThreatModelRoutes.Local,
            assessment: new ModelAssessment(
                "local_trained_model",
                0,
                null,
                "INCONCLUSIVE",
                [],
                "Local model was inconclusive."));
        var remoteAdapter = new TestModelAdapter(
            route: ThreatModelRoutes.Remote,
            assessment: new ModelAssessment(
                "openai_compatible_model",
                40,
                true,
                "MALICIOUS_BOT",
                ["model_verdict:malicious_bot"],
                "Remote model classified the request as malicious."));

        var service = CreateService(
            frequency: 1,
            reputationProviders: [],
            modelAdapters: [localAdapter, remoteAdapter],
            configure: options =>
            {
                options.Escalation.Routing.PreferredPrimaryRoute = ThreatModelRoutes.Local;
                options.Escalation.Routing.RemoteFallbackEnabled = true;
            });

        var result = await service.AssessAsync(new SuspiciousRequest(
            "198.51.100.32",
            "GET",
            "/probe",
            string.Empty,
            "crawler",
            ["generic_accept_any"],
            DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.True(result.ShouldBlock);
        Assert.Equal(1, localAdapter.CallCount);
        Assert.Equal(1, remoteAdapter.CallCount);
        Assert.Contains(result.Signals, signal => signal == "model_verdict:malicious_bot");
    }

    [Theory]
    [InlineData(30, ContainmentActions.Challenged, false)]
    [InlineData(45, ContainmentActions.Tarpitted, false)]
    [InlineData(58, ContainmentActions.Throttled, false)]
    public async Task AssessAsync_UsesContainmentPolicyBandsForNonBlockingActions(
        int scoreAdjustment,
        string expectedAction,
        bool shouldBlock)
    {
        var service = CreateService(
            frequency: 1,
            reputationProviders: [],
            modelAdapters:
            [
                new TestModelAdapter(new ModelAssessment(
                    "openai_compatible_model",
                    scoreAdjustment,
                    null,
                    "INCONCLUSIVE",
                    [],
                    "Remote model was inconclusive."))
            ],
            configure: options =>
            {
                options.Escalation.Containment.ChallengeScoreThreshold = 25;
                options.Escalation.Containment.TarpitScoreThreshold = 40;
                options.Escalation.Containment.ThrottleScoreThreshold = 55;
                options.Escalation.Containment.BlockScoreThreshold = 90;
                options.Escalation.Containment.FrequencyBlockThreshold = 50;
            });

        var result = await service.AssessAsync(new SuspiciousRequest(
            "198.51.100.33",
            "GET",
            "/probe",
            string.Empty,
            "crawler",
            [],
            DateTimeOffset.UtcNow), CancellationToken.None);

        Assert.Equal(expectedAction, result.Action);
        Assert.Equal(shouldBlock, result.ShouldBlock);
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
            new ThreatModelRoutingStrategy(Options.Create(options)),
            new ContainmentPolicyEngine(Options.Create(options)),
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
            : this(ThreatModelRoutes.Remote, assessment)
        {
        }

        public TestModelAdapter(string route, ModelAssessment? assessment)
        {
            Route = route;
            _assessment = assessment;
        }

        public string Name => "test_model";

        public string Route { get; }

        public int CallCount { get; private set; }

        public Task<ModelAssessment?> AssessAsync(ThreatAssessmentContext context, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_assessment);
        }
    }
}
