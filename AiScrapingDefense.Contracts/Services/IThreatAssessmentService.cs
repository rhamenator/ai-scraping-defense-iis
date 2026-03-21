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
    ContainmentDecision Evaluate(ThreatContainmentContributorContext context);
}

public interface IThreatScoreContributor
{
    string Name { get; }

    int Order { get; }

    Task<ThreatScoreContributorResult?> ContributeAsync(
        ThreatScoreContributorContext context,
        CancellationToken cancellationToken);
}

public interface IContainmentDecisionContributor
{
    string Name { get; }

    int Order { get; }

    ValueTask<ContainmentDecisionHint?> EvaluateAsync(
        ThreatContainmentContributorContext context,
        CancellationToken cancellationToken);
}

public interface IAssessmentTelemetry
{
    IDisposable? StartActivityScope(string name);

    void RecordAssessmentStage(string stage, string result);

    void RecordContributorExecution(string contributorType, string contributorName, string result);

    void RecordRoutingDecision(string primaryRoute, string effectiveRoute, bool fallbackEnabled);
}

public enum ThreatScoreContributionKind
{
    Custom = 0,
    BaseSignal = 1,
    Frequency = 2
}

public sealed record ThreatScoreContributorContext(
    SuspiciousRequest Request,
    long Frequency,
    IReadOnlyList<string> CurrentSignals,
    IReadOnlyList<DefenseScoreContribution> Contributions,
    int CurrentScore);

public sealed record ThreatScoreContributorResult(
    string Source,
    int ScoreAdjustment,
    IReadOnlyList<string> Signals,
    string Summary,
    ThreatScoreContributionKind Kind = ThreatScoreContributionKind.Custom,
    bool ExplicitMaliciousVerdict = false);

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

public sealed record ThreatContainmentContributorContext(
    ThreatAssessmentContext AssessmentContext,
    int TotalScore,
    bool ExplicitMaliciousVerdict,
    IReadOnlyList<string> Signals,
    IReadOnlyList<DefenseScoreContribution> Contributions);

public sealed record ContainmentDecisionHint(
    string Contributor,
    string Action,
    string Reason,
    bool ShouldBlock);

public sealed record ContainmentDecision(
    string Action,
    string Reason,
    bool ShouldBlock,
    string Contributor);

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
