using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public interface IThreatAssessmentService
{
    Task<ThreatAssessmentResult> AssessAsync(SuspiciousRequest request, CancellationToken cancellationToken);
}

public interface IThreatModelRoutingStrategy
{
    ThreatModelRoutingPlan BuildPlan(
        ThreatAssessmentContext context,
        IReadOnlyList<IThreatModelAdapter> adapters);
}

public interface IContainmentPolicyEngine
{
    ContainmentDecision Evaluate(ThreatAssessmentContext context, int totalScore, bool explicitMaliciousVerdict);
}

public sealed record ThreatAssessmentContext(
    string IpAddress,
    string Method,
    string Path,
    string QueryString,
    string UserAgent,
    IReadOnlyList<string> Signals,
    long Frequency,
    int BaseSignalScore,
    int FrequencyScore);

public sealed record ReputationAssessment(
    string Source,
    int ScoreAdjustment,
    bool IsMalicious,
    IReadOnlyList<string> Signals,
    string Summary);

public sealed record ModelAssessment(
    string Source,
    int ScoreAdjustment,
    bool? IsBot,
    string Classification,
    IReadOnlyList<string> Signals,
    string Summary);

public sealed record ThreatModelRoutingPlan(
    string PrimaryRoute,
    bool FallbackEnabled,
    IReadOnlyList<IThreatModelAdapter> OrderedAdapters);

public sealed record ContainmentDecision(
    string Action,
    string Reason,
    bool ShouldBlock);

public sealed record ThreatAssessmentResult(
    string Action,
    bool ShouldBlock,
    string DecisionReason,
    string Summary,
    int Score,
    long Frequency,
    IReadOnlyList<string> Signals,
    DefenseScoreBreakdown Breakdown)
{
    public string BlockReason => DecisionReason;
}
