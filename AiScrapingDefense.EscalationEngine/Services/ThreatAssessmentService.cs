using Microsoft.Extensions.Logging;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class ThreatAssessmentService : IThreatAssessmentService
{
    private readonly IRequestFrequencyTracker _frequencyTracker;
    private readonly IReadOnlyList<IThreatReputationProvider> _reputationProviders;
    private readonly IReadOnlyList<IThreatModelAdapter> _modelAdapters;
    private readonly IThreatModelRoutingStrategy _routingStrategy;
    private readonly IContainmentPolicyEngine _containmentPolicyEngine;
    private readonly ILogger<ThreatAssessmentService> _logger;

    public ThreatAssessmentService(
        IRequestFrequencyTracker frequencyTracker,
        IEnumerable<IThreatReputationProvider> reputationProviders,
        IEnumerable<IThreatModelAdapter> modelAdapters,
        IThreatModelRoutingStrategy routingStrategy,
        IContainmentPolicyEngine containmentPolicyEngine,
        ILogger<ThreatAssessmentService> logger)
    {
        _frequencyTracker = frequencyTracker;
        _reputationProviders = reputationProviders.ToArray();
        _modelAdapters = modelAdapters.ToArray();
        _routingStrategy = routingStrategy;
        _containmentPolicyEngine = containmentPolicyEngine;
        _logger = logger;
    }

    public async Task<ThreatAssessmentResult> AssessAsync(
        SuspiciousRequest request,
        CancellationToken cancellationToken)
    {
        var frequency = await _frequencyTracker.IncrementAsync(request.IpAddress, cancellationToken);
        var baseSignalScore = ScoreSignals(request.Signals);
        var frequencyScore = (int)Math.Min(25, frequency * 5);
        var context = new ThreatAssessmentContext(
            request.IpAddress,
            request.Method,
            request.Path,
            request.QueryString,
            request.UserAgent,
            request.Signals,
            frequency,
            baseSignalScore,
            frequencyScore);

        var combinedSignals = new List<string>(request.Signals);
        var contributions = new List<DefenseScoreContribution>();
        if (baseSignalScore > 0)
        {
            contributions.Add(new DefenseScoreContribution(
                "edge_signals",
                baseSignalScore,
                request.Signals,
                "Base edge heuristics contributed to the escalation score."));
        }

        if (frequencyScore > 0)
        {
            contributions.Add(new DefenseScoreContribution(
                "frequency",
                frequencyScore,
                ["frequency_window"],
                "Short-window suspicious request frequency increased the escalation score."));
        }

        var explicitMaliciousVerdict = false;
        var totalScore = baseSignalScore + frequencyScore;

        foreach (var provider in _reputationProviders)
        {
            var assessment = await provider.AssessAsync(context, cancellationToken);
            if (assessment is null)
            {
                continue;
            }

            totalScore += assessment.ScoreAdjustment;
            explicitMaliciousVerdict |= assessment.IsMalicious;
            AppendContribution(contributions, combinedSignals, assessment.Source, assessment.ScoreAdjustment, assessment.Signals, assessment.Summary);
        }

        var routingPlan = _routingStrategy.BuildPlan(context, _modelAdapters);
        var routedModelSignals = new List<string>
        {
            $"model_route:primary:{routingPlan.PrimaryRoute.ToLowerInvariant()}"
        };
        if (routingPlan.FallbackEnabled)
        {
            routedModelSignals.Add("model_route:fallback_enabled");
        }
        AppendContribution(
            contributions,
            combinedSignals,
            "model_routing",
            0,
            routedModelSignals,
            $"Model routing selected the {routingPlan.PrimaryRoute.ToLowerInvariant()} classifier route.");

        foreach (var adapter in routingPlan.OrderedAdapters)
        {
            var assessment = await adapter.AssessAsync(context, cancellationToken);
            if (assessment is null)
            {
                continue;
            }

            totalScore += assessment.ScoreAdjustment;
            explicitMaliciousVerdict |= assessment.IsBot == true;
            AppendContribution(contributions, combinedSignals, assessment.Source, assessment.ScoreAdjustment, assessment.Signals, assessment.Summary);

            if (assessment.IsBot is not null)
            {
                break;
            }
        }

        totalScore = Math.Max(0, totalScore);
        var containment = _containmentPolicyEngine.Evaluate(context, totalScore, explicitMaliciousVerdict);
        var summary = BuildSummary(containment.Action, totalScore, frequency, explicitMaliciousVerdict, contributions);

        _logger.LogInformation(
            "Threat assessment completed for {IpAddress} with score {Score}, frequency {Frequency}, explicit malicious verdict {ExplicitVerdict}, action {Action}.",
            request.IpAddress,
            totalScore,
            frequency,
            explicitMaliciousVerdict,
            containment.Action);

        return new ThreatAssessmentResult(
            containment.Action,
            containment.ShouldBlock,
            containment.Reason,
            summary,
            totalScore,
            frequency,
            combinedSignals.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            new DefenseScoreBreakdown(
                baseSignalScore,
                frequencyScore,
                totalScore,
                explicitMaliciousVerdict,
                contributions));
    }

    private static int ScoreSignals(IReadOnlyList<string> signals)
    {
        var score = 0;

        foreach (var signal in signals)
        {
            if (signal.StartsWith("known_bad_user_agent:", StringComparison.Ordinal))
            {
                score += 100;
            }
            else if (signal.StartsWith("suspicious_path:", StringComparison.Ordinal))
            {
                score += 30;
            }
            else if (string.Equals(signal, "empty_user_agent", StringComparison.Ordinal))
            {
                score += 25;
            }
            else if (string.Equals(signal, "missing_accept_language", StringComparison.Ordinal))
            {
                score += 15;
            }
            else if (string.Equals(signal, "generic_accept_any", StringComparison.Ordinal))
            {
                score += 15;
            }
            else if (string.Equals(signal, "long_query_string", StringComparison.Ordinal))
            {
                score += 10;
            }
        }

        return score;
    }

    private static void AppendContribution(
        ICollection<DefenseScoreContribution> contributions,
        ICollection<string> combinedSignals,
        string source,
        int scoreDelta,
        IReadOnlyList<string> signals,
        string summary)
    {
        if (scoreDelta == 0 && signals.Count == 0 && string.IsNullOrWhiteSpace(summary))
        {
            return;
        }

        contributions.Add(new DefenseScoreContribution(
            source,
            scoreDelta,
            signals,
            summary));

        foreach (var signal in signals)
        {
            combinedSignals.Add(signal);
        }
    }

    private static string BuildSummary(
        string action,
        int totalScore,
        long frequency,
        bool explicitMaliciousVerdict,
        IReadOnlyList<DefenseScoreContribution> contributions)
    {
        var dominantSources = contributions
            .OrderByDescending(contribution => Math.Abs(contribution.ScoreDelta))
            .Take(3)
            .Select(contribution => contribution.Source)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var verdictText = explicitMaliciousVerdict ? " explicit malicious verdict;" : string.Empty;
        var sourceText = dominantSources.Length == 0
            ? " no named contributors."
            : " top contributors: " + string.Join(", ", dominantSources) + ".";

        return $"Queued analysis {action} the request with total score {totalScore}, frequency {frequency},{verdictText}{sourceText}";
    }
}
