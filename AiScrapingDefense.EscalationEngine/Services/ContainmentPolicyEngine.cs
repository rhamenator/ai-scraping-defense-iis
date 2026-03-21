using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class ContainmentPolicyEngine : IContainmentPolicyEngine
{
    private readonly IReadOnlyList<IContainmentDecisionContributor> _contributors;
    private readonly IAssessmentTelemetry _telemetry;
    private readonly ILogger<ContainmentPolicyEngine> _logger;

    public ContainmentPolicyEngine(
        IEnumerable<IContainmentDecisionContributor> contributors,
        IAssessmentTelemetry telemetry,
        ILogger<ContainmentPolicyEngine> logger)
    {
        _contributors = contributors
            .OrderBy(contributor => contributor.Order)
            .ThenBy(contributor => contributor.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _telemetry = telemetry;
        _logger = logger;
    }

    public ContainmentDecision Evaluate(ThreatContainmentContributorContext context)
    {
        foreach (var contributor in _contributors)
        {
            try
            {
                var hint = contributor.EvaluateAsync(context, CancellationToken.None).AsTask().GetAwaiter().GetResult();
                if (hint is null)
                {
                    _telemetry.RecordContributorExecution("containment", contributor.Name, "skipped");
                    continue;
                }

                _telemetry.RecordContributorExecution("containment", contributor.Name, "applied");
                return new ContainmentDecision(
                    hint.Action,
                    hint.Reason,
                    hint.ShouldBlock,
                    hint.Contributor);
            }
            catch (Exception ex)
            {
                _telemetry.RecordContributorExecution("containment", contributor.Name, "failed");
                _logger.LogWarning(ex, "Containment contributor {ContributorName} failed.", contributor.Name);
            }
        }

        return new ContainmentDecision(
            ContainmentActions.Observed,
            "queued_analysis_observed",
            ShouldBlock: false,
            Contributor: "fallback");
    }
}