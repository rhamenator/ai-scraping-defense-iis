using System.Diagnostics;
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
    private readonly IAssessmentTelemetry _telemetry;
    private readonly ILogger<ThreatAssessmentService> _logger;

    public ThreatAssessmentService(
        IRequestFrequencyTracker frequencyTracker,
        IEnumerable<IThreatReputationProvider> reputationProviders,
        IEnumerable<IThreatModelAdapter> modelAdapters,
        IThreatModelRoutingStrategy routingStrategy,
        IContainmentPolicyEngine containmentPolicyEngine,
        IAssessmentTelemetry telemetry,
        ILogger<ThreatAssessmentService> logger)
    {
        _frequencyTracker = frequencyTracker;
        _reputationProviders = reputationProviders.ToArray();
        _modelAdapters = modelAdapters.ToArray();
        _routingStrategy = routingStrategy;
        _containmentPolicyEngine = containmentPolicyEngine;
        _telemetry = telemetry;
        _logger = logger;
    }

    public async Task<ThreatAssessmentResult> AssessAsync(
        SuspiciousRequest request,
        CancellationToken cancellationToken)
    {
        using var assessmentActivity = _telemetry.StartActivityScope("assessment.evaluate") as Activity;
        assessmentActivity?.SetTag("assessment.ip", request.IpAddress);
        assessmentActivity?.SetTag("assessment.path", request.Path);
        assessmentActivity?.SetTag("assessment.signal_count", request.Signals.Count);

        long frequency;
        using (var frequencyActivity = _telemetry.StartActivityScope("assessment.frequency") as Activity)
        {
            frequency = await _frequencyTracker.IncrementAsync(request.IpAddress, cancellationToken);
            frequencyActivity?.SetTag("frequency.value", frequency);
            _telemetry.RecordAssessmentStage("frequency", "measured");
        }

        var baseSignalScore = ScoreSignals(request.Signals);
        var frequencyScore = (int)Math.Min(25, frequency * 5);
        assessmentActivity?.SetTag("assessment.base_signal_score", baseSignalScore);
        assessmentActivity?.SetTag("assessment.frequency_score", frequencyScore);
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
        var adapterVerdicts = new List<DefenseAdapterVerdict>();

        foreach (var provider in _reputationProviders)
        {
            using var providerActivity = _telemetry.StartActivityScope("assessment.reputation_provider") as Activity;
            providerActivity?.SetTag("provider.name", provider.Name);
            var assessment = await provider.AssessAsync(context, cancellationToken);
            if (assessment is null)
            {
                _telemetry.RecordAssessmentStage("reputation_provider", "miss");
                continue;
            }

            totalScore += assessment.ScoreAdjustment;
            explicitMaliciousVerdict |= assessment.IsMalicious;
            AppendContribution(contributions, combinedSignals, assessment.Source, assessment.ScoreAdjustment, assessment.Signals, assessment.Summary);
            providerActivity?.SetTag("provider.source", assessment.Source);
            providerActivity?.SetTag("provider.score_delta", assessment.ScoreAdjustment);
            providerActivity?.SetTag("provider.is_malicious", assessment.IsMalicious);
            _telemetry.RecordAssessmentStage("reputation_provider", assessment.IsMalicious ? "malicious" : "matched");
        }

        ThreatModelRoutingPlan routingPlan;
        using (var routingActivity = _telemetry.StartActivityScope("assessment.routing") as Activity)
        {
            routingPlan = _routingStrategy.BuildPlan(context, _modelAdapters);
            routingActivity?.SetTag("routing.primary_route", routingPlan.PrimaryRoute);
            routingActivity?.SetTag("routing.fallback_enabled", routingPlan.FallbackEnabled);
            routingActivity?.SetTag("routing.adapter_count", routingPlan.OrderedAdapters.Count);
            _telemetry.RecordAssessmentStage("routing", routingPlan.PrimaryRoute);
        }

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

        var effectiveRoute = routingPlan.PrimaryRoute;
        var evaluatedAdapters = new List<string>();

        foreach (var adapter in routingPlan.OrderedAdapters)
        {
            using var adapterActivity = _telemetry.StartActivityScope("assessment.model_adapter") as Activity;
            adapterActivity?.SetTag("adapter.name", adapter.Name);
            adapterActivity?.SetTag("adapter.route", adapter.Route);

            var assessment = await adapter.AssessAsync(context, cancellationToken);
            if (assessment is null)
            {
                _telemetry.RecordAssessmentStage("model_adapter", "no_result");
                continue;
            }

            totalScore += assessment.ScoreAdjustment;
            explicitMaliciousVerdict |= assessment.IsBot == true;
            AppendContribution(contributions, combinedSignals, assessment.Source, assessment.ScoreAdjustment, assessment.Signals, assessment.Summary);
            var decisive = assessment.IsBot is not null;
            effectiveRoute = adapter.Route;
            evaluatedAdapters.Add($"{adapter.Route}:{adapter.Name}");
            adapterVerdicts.Add(new DefenseAdapterVerdict(
                adapter.Name,
                adapter.Route,
                assessment.Classification,
                assessment.IsBot,
                assessment.ScoreAdjustment,
                decisive,
                assessment.Signals,
                assessment.Summary));
            adapterActivity?.SetTag("adapter.classification", assessment.Classification);
            adapterActivity?.SetTag("adapter.score_delta", assessment.ScoreAdjustment);
            adapterActivity?.SetTag("adapter.is_bot", assessment.IsBot?.ToString() ?? "inconclusive");
            adapterActivity?.SetTag("adapter.decisive", decisive);
            _telemetry.RecordAssessmentStage("model_adapter", decisive ? "decisive" : "inconclusive");

            if (decisive)
            {
                break;
            }
        }

        totalScore = Math.Max(0, totalScore);
        ContainmentDecision containment;
        using (var containmentActivity = _telemetry.StartActivityScope("assessment.containment") as Activity)
        {
            containment = _containmentPolicyEngine.Evaluate(context, totalScore, explicitMaliciousVerdict);
            containmentActivity?.SetTag("containment.action", containment.Action);
            containmentActivity?.SetTag("containment.reason", containment.Reason);
            containmentActivity?.SetTag("containment.should_block", containment.ShouldBlock);
            _telemetry.RecordAssessmentStage("containment", containment.Action);
        }

        var routingDetails = new DefenseRoutingDetails(
            routingPlan.PrimaryRoute,
            effectiveRoute,
            routingPlan.FallbackEnabled,
            routingPlan.OrderedAdapters.Select(adapter => $"{adapter.Route}:{adapter.Name}").ToArray(),
            evaluatedAdapters);
        var containmentDetails = new DefenseContainmentDetails(
            containment.Action,
            containment.Reason,
            containment.ShouldBlock);
        _telemetry.RecordRoutingDecision(routingDetails.PrimaryRoute, routingDetails.EffectiveRoute, routingDetails.FallbackEnabled);

        var summary = BuildSummary(containment.Action, totalScore, frequency, explicitMaliciousVerdict, contributions);
        assessmentActivity?.SetTag("assessment.total_score", totalScore);
        assessmentActivity?.SetTag("assessment.action", containment.Action);
        assessmentActivity?.SetTag("assessment.reason", containment.Reason);
        assessmentActivity?.SetTag("assessment.effective_route", routingDetails.EffectiveRoute);

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
                contributions,
                adapterVerdicts,
                routingDetails,
                containmentDetails));
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
