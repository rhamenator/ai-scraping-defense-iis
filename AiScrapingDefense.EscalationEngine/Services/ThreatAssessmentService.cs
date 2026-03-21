using System.Diagnostics;
using Microsoft.Extensions.Logging;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class ThreatAssessmentService : IThreatAssessmentService
{
    private readonly IRequestFrequencyTracker _frequencyTracker;
    private readonly IReadOnlyList<IThreatScoreContributor> _scoreContributors;
    private readonly IReadOnlyList<IThreatReputationProvider> _reputationProviders;
    private readonly IReadOnlyList<IThreatModelAdapter> _modelAdapters;
    private readonly IThreatModelRoutingStrategy _routingStrategy;
    private readonly IContainmentPolicyEngine _containmentPolicyEngine;
    private readonly IAssessmentTelemetry _telemetry;
    private readonly ILogger<ThreatAssessmentService> _logger;

    public ThreatAssessmentService(
        IRequestFrequencyTracker frequencyTracker,
        IEnumerable<IThreatScoreContributor> scoreContributors,
        IEnumerable<IThreatReputationProvider> reputationProviders,
        IEnumerable<IThreatModelAdapter> modelAdapters,
        IThreatModelRoutingStrategy routingStrategy,
        IContainmentPolicyEngine containmentPolicyEngine,
        IAssessmentTelemetry telemetry,
        ILogger<ThreatAssessmentService> logger)
    {
        _frequencyTracker = frequencyTracker;
        _scoreContributors = scoreContributors
            .OrderBy(contributor => contributor.Order)
            .ThenBy(contributor => contributor.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
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

        var combinedSignals = new List<string>(request.Signals);
        var contributions = new List<DefenseScoreContribution>();
        var explicitMaliciousVerdict = false;
        var totalScore = 0;
        var baseSignalScore = 0;
        var frequencyScore = 0;
        var adapterVerdicts = new List<DefenseAdapterVerdict>();

        foreach (var contributor in _scoreContributors)
        {
            using var contributorActivity = _telemetry.StartActivityScope("assessment.score_contributor") as Activity;
            contributorActivity?.SetTag("contributor.type", "score");
            contributorActivity?.SetTag("contributor.name", contributor.Name);
            contributorActivity?.SetTag("contributor.order", contributor.Order);

            try
            {
                var contribution = await contributor.ContributeAsync(
                    new ThreatScoreContributorContext(
                        request,
                        frequency,
                        combinedSignals.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                        contributions.ToArray(),
                        totalScore),
                    cancellationToken);
                if (contribution is null)
                {
                    _telemetry.RecordContributorExecution("score", contributor.Name, "skipped");
                    continue;
                }

                totalScore += contribution.ScoreAdjustment;
                explicitMaliciousVerdict |= contribution.ExplicitMaliciousVerdict;
                baseSignalScore += contribution.Kind == ThreatScoreContributionKind.BaseSignal
                    ? contribution.ScoreAdjustment
                    : 0;
                frequencyScore += contribution.Kind == ThreatScoreContributionKind.Frequency
                    ? contribution.ScoreAdjustment
                    : 0;
                AppendContribution(
                    contributions,
                    combinedSignals,
                    contribution.Source,
                    contribution.ScoreAdjustment,
                    contribution.Signals,
                    contribution.Summary);
                contributorActivity?.SetTag("contributor.result", "applied");
                contributorActivity?.SetTag("contributor.source", contribution.Source);
                contributorActivity?.SetTag("contributor.score_delta", contribution.ScoreAdjustment);
                contributorActivity?.SetTag("contributor.kind", contribution.Kind.ToString());
                _telemetry.RecordContributorExecution("score", contributor.Name, "applied");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                contributorActivity?.SetTag("contributor.result", "failed");
                contributorActivity?.SetTag("contributor.failure", ex.GetType().Name);
                _telemetry.RecordContributorExecution("score", contributor.Name, "failed");
                _logger.LogWarning(ex, "Threat score contributor {ContributorName} failed.", contributor.Name);
            }
        }

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
            containment = _containmentPolicyEngine.Evaluate(new ThreatContainmentContributorContext(
                context,
                totalScore,
                explicitMaliciousVerdict,
                combinedSignals.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                contributions.ToArray()));
            containmentActivity?.SetTag("containment.action", containment.Action);
            containmentActivity?.SetTag("containment.reason", containment.Reason);
            containmentActivity?.SetTag("containment.should_block", containment.ShouldBlock);
            containmentActivity?.SetTag("containment.contributor", containment.Contributor);
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
            containment.ShouldBlock,
            containment.Contributor);
        _telemetry.RecordRoutingDecision(routingDetails.PrimaryRoute, routingDetails.EffectiveRoute, routingDetails.FallbackEnabled);

        var summary = BuildSummary(containment.Action, totalScore, frequency, explicitMaliciousVerdict, contributions);
        assessmentActivity?.SetTag("assessment.total_score", totalScore);
        assessmentActivity?.SetTag("assessment.action", containment.Action);
        assessmentActivity?.SetTag("assessment.reason", containment.Reason);
        assessmentActivity?.SetTag("assessment.containment_contributor", containment.Contributor);
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
