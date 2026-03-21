using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class ExplicitVerdictContainmentContributor : IContainmentDecisionContributor
{
    public string Name => "explicit_verdict";

    public int Order => 0;

    public ValueTask<ContainmentDecisionHint?> EvaluateAsync(
        ThreatContainmentContributorContext context,
        CancellationToken cancellationToken)
    {
        if (!context.ExplicitMaliciousVerdict)
        {
            return ValueTask.FromResult<ContainmentDecisionHint?>(null);
        }

        return ValueTask.FromResult<ContainmentDecisionHint?>(new ContainmentDecisionHint(
            Name,
            ContainmentActions.Blocked,
            "threat_intelligence_verdict",
            ShouldBlock: true));
    }
}