using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class OperatorRecommendationService : IOperatorRecommendationService
{
    private readonly IDefenseEventStore _store;
    private readonly DefenseEngineOptions _options;
    private readonly TimeProvider _timeProvider;

    public OperatorRecommendationService(
        IDefenseEventStore store,
        IOptions<DefenseEngineOptions> options,
        TimeProvider timeProvider)
    {
        _store = store;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public OperatorRecommendationSnapshot GetRecommendations()
    {
        var recentDecisions = _store.GetRecent(100);
        var recommendations = new List<OperatorRecommendation>();

        if (recentDecisions.Count > 0)
        {
            AddBlockThresholdRecommendations(recentDecisions, recommendations);
            AddFrequencyThresholdRecommendation(recentDecisions, recommendations);
            AddHotPathRecommendation(recentDecisions, recommendations);
        }

        return new OperatorRecommendationSnapshot(
            _timeProvider.GetUtcNow(),
            recentDecisions.Count,
            recommendations);
    }

    private void AddBlockThresholdRecommendations(
        IReadOnlyList<DefenseDecision> recentDecisions,
        ICollection<OperatorRecommendation> recommendations)
    {
        var blockedCount = recentDecisions.Count(IsBlocked);
        var blockedRate = blockedCount / (double)recentDecisions.Count;
        var blockThreshold = _options.Escalation.Containment.BlockScoreThreshold;
        var nearBlockObservedCount = recentDecisions.Count(decision =>
            !IsBlocked(decision) &&
            decision.Score >= Math.Max(0, blockThreshold - 10));

        if (recentDecisions.Count >= 20 && blockedRate >= 0.65d)
        {
            var suggestedThreshold = Math.Min(100, blockThreshold + 5);
            if (suggestedThreshold > blockThreshold)
            {
                recommendations.Add(new OperatorRecommendation(
                    "raise-block-threshold",
                    "containment",
                    "medium",
                    "Raise the queued-analysis block threshold",
                    $"{blockedCount} of the last {recentDecisions.Count} decisions ended in an outright block.",
                    "A very high block rate can indicate that the current containment threshold is too aggressive for mixed traffic.",
                    $"Block score threshold = {blockThreshold}",
                    $"Increase block score threshold to {suggestedThreshold}",
                    [
                        $"Blocked-rate sample: {blockedRate:P0}",
                        $"Current containment block threshold: {blockThreshold}"
                    ]));
            }
        }

        if (recentDecisions.Count >= 20 && blockedRate <= 0.15d && nearBlockObservedCount >= 5)
        {
            var suggestedThreshold = Math.Max(10, blockThreshold - 5);
            if (suggestedThreshold < blockThreshold)
            {
                recommendations.Add(new OperatorRecommendation(
                    "lower-block-threshold",
                    "containment",
                    "medium",
                    "Lower the queued-analysis block threshold",
                    $"{nearBlockObservedCount} recent decisions scored near the block threshold but remained non-blocking.",
                    "The current threshold may be letting obviously risky requests stay in challenge or observe-only states for too long.",
                    $"Block score threshold = {blockThreshold}",
                    $"Decrease block score threshold to {suggestedThreshold}",
                    [
                        $"Blocked-rate sample: {blockedRate:P0}",
                        $"Near-block non-blocking decisions: {nearBlockObservedCount}"
                    ]));
            }
        }
    }

    private void AddFrequencyThresholdRecommendation(
        IReadOnlyList<DefenseDecision> recentDecisions,
        ICollection<OperatorRecommendation> recommendations)
    {
        var highFrequencyObservedCount = recentDecisions.Count(decision =>
            !IsBlocked(decision) &&
            decision.Frequency >= _options.Escalation.Containment.FrequencyBlockThreshold);

        if (highFrequencyObservedCount < 5)
        {
            return;
        }

        var currentThreshold = _options.Escalation.Containment.FrequencyBlockThreshold;
        var suggestedThreshold = Math.Max(1, currentThreshold - 1);
        if (suggestedThreshold == currentThreshold)
        {
            return;
        }

        recommendations.Add(new OperatorRecommendation(
            "lower-frequency-threshold",
            "containment",
            "low",
            "Lower the frequency block threshold",
            $"{highFrequencyObservedCount} recent decisions reached the current frequency threshold without being blocked.",
            "This pattern suggests the queue is still seeing repeated bursts from the same sources after the configured frequency threshold is met.",
            $"Frequency block threshold = {currentThreshold}",
            $"Decrease frequency block threshold to {suggestedThreshold}",
            [
                $"Observed high-frequency non-blocking decisions: {highFrequencyObservedCount}",
                $"Current containment frequency threshold: {currentThreshold}"
            ]));
    }

    private static void AddHotPathRecommendation(
        IReadOnlyList<DefenseDecision> recentDecisions,
        ICollection<OperatorRecommendation> recommendations)
    {
        if (recentDecisions.Count < 15)
        {
            return;
        }

        var dominantPath = recentDecisions
            .Where(decision => !string.IsNullOrWhiteSpace(decision.Path))
            .GroupBy(decision => decision.Path, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count())
            .FirstOrDefault();

        if (dominantPath is null)
        {
            return;
        }

        var dominantPathCount = dominantPath.Count();
        var threshold = Math.Max(5, (int)Math.Ceiling(recentDecisions.Count * 0.4d));
        if (dominantPathCount < threshold)
        {
            return;
        }

        recommendations.Add(new OperatorRecommendation(
            "review-hot-path",
            "traffic-shaping",
            "low",
            "Review the busiest defended path",
            $"The path {dominantPath.Key} accounts for {dominantPathCount} of the last {recentDecisions.Count} decisions.",
            "A single dominant route can usually be tuned more safely with a targeted rule or allowlist than with a stack-wide threshold change.",
            "Not evaluated",
            $"Review a dedicated rule or allowlist policy for {dominantPath.Key}",
            [
                $"Dominant path sample: {dominantPath.Key}",
                $"Recent decision share: {(dominantPathCount / (double)recentDecisions.Count):P0}"
            ]));
    }

    private static bool IsBlocked(DefenseDecision decision)
    {
        return string.Equals(decision.Action, "blocked", StringComparison.OrdinalIgnoreCase);
    }
}