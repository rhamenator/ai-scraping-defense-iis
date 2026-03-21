using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class OperatorRecommendationServiceTests
{
    [Fact]
    public void GetRecommendations_SuggestsRaisingBlockThreshold_WhenBlockRateIsVeryHigh()
    {
        var observedAtUtc = DateTimeOffset.UtcNow;
        var decisions = Enumerable.Range(0, 20)
            .Select(index => new DefenseDecision(
                $"198.51.100.{index}",
                index < 15 ? "blocked" : "observed",
                index < 15 ? 75 : 30,
                1,
                "/docs",
                ["signal"],
                "summary",
                observedAtUtc,
                observedAtUtc))
            .ToArray();
        var service = CreateService(decisions, options =>
        {
            options.Escalation.Containment.BlockScoreThreshold = 60;
        });

        var snapshot = service.GetRecommendations();

        var recommendation = Assert.Single(snapshot.Recommendations, item => item.Id == "raise-block-threshold");
        Assert.Contains("Increase block score threshold to 65", recommendation.SuggestedValue, StringComparison.Ordinal);
        Assert.Contains("outright block", recommendation.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetRecommendations_SuggestsLoweringThresholds_WhenRiskyTrafficStaysNonBlocking()
    {
        var observedAtUtc = DateTimeOffset.UtcNow;
        var decisions = Enumerable.Range(0, 24)
            .Select(index => new DefenseDecision(
                $"198.51.100.{index}",
                index < 3 ? "blocked" : "observed",
                index < 3 ? 90 : 55,
                index < 10 ? 8 : 2,
                "/search",
                ["signal"],
                "summary",
                observedAtUtc,
                observedAtUtc))
            .ToArray();
        var service = CreateService(decisions, options =>
        {
            options.Escalation.Containment.BlockScoreThreshold = 60;
            options.Escalation.Containment.FrequencyBlockThreshold = 8;
        });

        var snapshot = service.GetRecommendations();

        Assert.Contains(snapshot.Recommendations, item => item.Id == "lower-block-threshold");
        Assert.Contains(snapshot.Recommendations, item => item.Id == "lower-frequency-threshold");
        Assert.Contains(snapshot.Recommendations, item => item.Id == "review-hot-path");
    }

    private static OperatorRecommendationService CreateService(
        IReadOnlyList<DefenseDecision> decisions,
        Action<DefenseEngineOptions>? configure = null)
    {
        var options = new DefenseEngineOptions
        {
            Escalation = new EscalationOptions
            {
                Containment = new ContainmentPolicyOptions
                {
                    BlockScoreThreshold = 60,
                    FrequencyBlockThreshold = 8
                }
            }
        };
        configure?.Invoke(options);

        return new OperatorRecommendationService(
            new TestDefenseEventStore(decisions),
            Options.Create(options),
            TimeProvider.System);
    }

    private sealed class TestDefenseEventStore : IDefenseEventStore
    {
        private readonly IReadOnlyList<DefenseDecision> _decisions;

        public TestDefenseEventStore(IReadOnlyList<DefenseDecision> decisions)
        {
            _decisions = decisions;
        }

        public void Add(DefenseDecision decision)
        {
        }

        public IReadOnlyList<DefenseDecision> GetRecent(int count)
        {
            return _decisions.Take(count).ToArray();
        }

        public DefenseEventMetrics GetMetrics()
        {
            return new DefenseEventMetrics(_decisions.Count, _decisions.Count(item => item.Action == "blocked"), _decisions.Count(item => item.Action == "observed"), _decisions.LastOrDefault()?.DecidedAtUtc);
        }
    }
}