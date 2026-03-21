using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class ContainmentPolicyEngine : IContainmentPolicyEngine
{
    private readonly ContainmentPolicyOptions _options;

    public ContainmentPolicyEngine(IOptions<DefenseEngineOptions> options)
    {
        _options = options.Value.Escalation.Containment;
    }

    public ContainmentDecision Evaluate(
        ThreatAssessmentContext context,
        int totalScore,
        bool explicitMaliciousVerdict)
    {
        if (explicitMaliciousVerdict)
        {
            return new ContainmentDecision(
                ContainmentActions.Blocked,
                "threat_intelligence_verdict",
                ShouldBlock: true);
        }

        if (context.Frequency >= _options.FrequencyBlockThreshold)
        {
            return new ContainmentDecision(
                ContainmentActions.Blocked,
                "frequency_threshold",
                ShouldBlock: true);
        }

        if (totalScore >= _options.BlockScoreThreshold)
        {
            return new ContainmentDecision(
                ContainmentActions.Blocked,
                "queued_analysis_threshold",
                ShouldBlock: true);
        }

        if (totalScore >= _options.ThrottleScoreThreshold)
        {
            return new ContainmentDecision(
                ContainmentActions.Throttled,
                "score_policy_throttle",
                ShouldBlock: false);
        }

        if (totalScore >= _options.TarpitScoreThreshold)
        {
            return new ContainmentDecision(
                ContainmentActions.Tarpitted,
                "score_policy_tarpit",
                ShouldBlock: false);
        }

        if (totalScore >= _options.ChallengeScoreThreshold)
        {
            return new ContainmentDecision(
                ContainmentActions.Challenged,
                "score_policy_challenge",
                ShouldBlock: false);
        }

        return new ContainmentDecision(
            ContainmentActions.Observed,
            "queued_analysis_observed",
            ShouldBlock: false);
    }
}