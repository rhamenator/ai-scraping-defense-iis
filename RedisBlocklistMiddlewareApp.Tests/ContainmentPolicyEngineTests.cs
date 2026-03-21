using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class ContainmentPolicyEngineTests
{
    [Fact]
    public void Evaluate_UsesOrderedContributorsAndSkipsFailingContributors()
    {
        var executionOrder = new List<string>();
        var engine = new ContainmentPolicyEngine(
            [
                new ThrowingContainmentContributor("custom_failure", 50, executionOrder),
                new TrackingContainmentContributor("custom_override", 60, executionOrder, new ContainmentDecisionHint(
                    "custom_override",
                    ContainmentActions.Challenged,
                    "custom_override_reason",
                    ShouldBlock: false)),
                new TrackingContainmentContributor("should_not_run", 70, executionOrder, new ContainmentDecisionHint(
                    "should_not_run",
                    ContainmentActions.Blocked,
                    "should_not_run",
                    ShouldBlock: true))
            ],
            TestTelemetryFactory.Create(),
            NullLogger<ContainmentPolicyEngine>.Instance);

        var decision = engine.Evaluate(CreateContext(totalScore: 10, explicitMaliciousVerdict: false));

        Assert.Equal(["custom_failure", "custom_override"], executionOrder);
        Assert.Equal(ContainmentActions.Challenged, decision.Action);
        Assert.Equal("custom_override_reason", decision.Reason);
        Assert.Equal("custom_override", decision.Contributor);
    }

    [Fact]
    public void Evaluate_FallsBackToThresholdContributorWhenNoOverrideMatches()
    {
        var options = Options.Create(new DefenseEngineOptions
        {
            Escalation = new EscalationOptions
            {
                Containment = new ContainmentPolicyOptions
                {
                    ChallengeScoreThreshold = 25,
                    TarpitScoreThreshold = 40,
                    ThrottleScoreThreshold = 55,
                    BlockScoreThreshold = 90,
                    FrequencyBlockThreshold = 50
                }
            }
        });
        var engine = new ContainmentPolicyEngine(
            [
                new ExplicitVerdictContainmentContributor(),
                new FrequencyContainmentContributor(options),
                new ThresholdBandContainmentContributor(options)
            ],
            TestTelemetryFactory.Create(),
            NullLogger<ContainmentPolicyEngine>.Instance);

        var decision = engine.Evaluate(CreateContext(totalScore: 56, explicitMaliciousVerdict: false));

        Assert.Equal(ContainmentActions.Throttled, decision.Action);
        Assert.Equal("score_policy_throttle", decision.Reason);
        Assert.Equal("score_thresholds", decision.Contributor);
    }

    private static ThreatContainmentContributorContext CreateContext(int totalScore, bool explicitMaliciousVerdict)
    {
        return new ThreatContainmentContributorContext(
            new ThreatAssessmentContext(
                "198.51.100.50",
                "GET",
                "/test",
                string.Empty,
                "crawler",
                [],
                Frequency: 1,
                BaseSignalScore: 0,
                FrequencyScore: 0),
            totalScore,
            explicitMaliciousVerdict,
            [],
            []);
    }

    private sealed class TrackingContainmentContributor : IContainmentDecisionContributor
    {
        private readonly IList<string> _executionOrder;
        private readonly ContainmentDecisionHint? _hint;

        public TrackingContainmentContributor(
            string name,
            int order,
            IList<string> executionOrder,
            ContainmentDecisionHint? hint)
        {
            Name = name;
            Order = order;
            _executionOrder = executionOrder;
            _hint = hint;
        }

        public string Name { get; }

        public int Order { get; }

        public ValueTask<ContainmentDecisionHint?> EvaluateAsync(
            ThreatContainmentContributorContext context,
            CancellationToken cancellationToken)
        {
            _executionOrder.Add(Name);
            return ValueTask.FromResult(_hint);
        }
    }

    private sealed class ThrowingContainmentContributor : IContainmentDecisionContributor
    {
        private readonly IList<string> _executionOrder;

        public ThrowingContainmentContributor(string name, int order, IList<string> executionOrder)
        {
            Name = name;
            Order = order;
            _executionOrder = executionOrder;
        }

        public string Name { get; }

        public int Order { get; }

        public ValueTask<ContainmentDecisionHint?> EvaluateAsync(
            ThreatContainmentContributorContext context,
            CancellationToken cancellationToken)
        {
            _executionOrder.Add(Name);
            throw new InvalidOperationException("Containment contributor failure.");
        }
    }
}
