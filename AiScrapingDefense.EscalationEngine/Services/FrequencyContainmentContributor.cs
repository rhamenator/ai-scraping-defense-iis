using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class FrequencyContainmentContributor : IContainmentDecisionContributor
{
    private readonly ContainmentPolicyOptions _options;

    public FrequencyContainmentContributor(IOptions<DefenseEngineOptions> options)
    {
        _options = options.Value.Escalation.Containment;
    }

    public string Name => "frequency_threshold";

    public int Order => 100;

    public ValueTask<ContainmentDecisionHint?> EvaluateAsync(
        ThreatContainmentContributorContext context,
        CancellationToken cancellationToken)
    {
        if (context.AssessmentContext.Frequency < _options.FrequencyBlockThreshold)
        {
            return ValueTask.FromResult<ContainmentDecisionHint?>(null);
        }

        return ValueTask.FromResult<ContainmentDecisionHint?>(new ContainmentDecisionHint(
            Name,
            ContainmentActions.Blocked,
            "frequency_threshold",
            ShouldBlock: true));
    }
}