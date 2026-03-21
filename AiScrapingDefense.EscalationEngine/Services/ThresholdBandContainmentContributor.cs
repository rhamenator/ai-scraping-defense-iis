using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class ThresholdBandContainmentContributor : IContainmentDecisionContributor
{
    private readonly ContainmentPolicyOptions _options;

    public ThresholdBandContainmentContributor(IOptions<DefenseEngineOptions> options)
    {
        _options = options.Value.Escalation.Containment;
    }

    public string Name => "score_thresholds";

    public int Order => 200;

    public ValueTask<ContainmentDecisionHint?> EvaluateAsync(
        ThreatContainmentContributorContext context,
        CancellationToken cancellationToken)
    {
        if (context.TotalScore >= _options.BlockScoreThreshold)
        {
            return CreateHint(ContainmentActions.Blocked, "queued_analysis_threshold", shouldBlock: true);
        }

        if (context.TotalScore >= _options.ThrottleScoreThreshold)
        {
            return CreateHint(ContainmentActions.Throttled, "score_policy_throttle", shouldBlock: false);
        }

        if (context.TotalScore >= _options.TarpitScoreThreshold)
        {
            return CreateHint(ContainmentActions.Tarpitted, "score_policy_tarpit", shouldBlock: false);
        }

        if (context.TotalScore >= _options.ChallengeScoreThreshold)
        {
            return CreateHint(ContainmentActions.Challenged, "score_policy_challenge", shouldBlock: false);
        }

        return CreateHint(ContainmentActions.Observed, "queued_analysis_observed", shouldBlock: false);
    }

    private ValueTask<ContainmentDecisionHint?> CreateHint(string action, string reason, bool shouldBlock)
    {
        return ValueTask.FromResult<ContainmentDecisionHint?>(new ContainmentDecisionHint(
            Name,
            action,
            reason,
            shouldBlock));
    }
}