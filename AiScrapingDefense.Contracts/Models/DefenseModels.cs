namespace RedisBlocklistMiddlewareApp.Models;

using System.Text.Json.Serialization;

public sealed record SuspiciousRequest(
    string IpAddress,
    string Method,
    string Path,
    string QueryString,
    string UserAgent,
    IReadOnlyList<string> Signals,
    DateTimeOffset ObservedAtUtc);

public sealed record DefenseDecision(
    string IpAddress,
    string Action,
    int Score,
    long Frequency,
    string Path,
    IReadOnlyList<string> Signals,
    string Summary,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset DecidedAtUtc,
    DefenseScoreBreakdown? Breakdown = null,
    long Id = 0);

public sealed record DefenseScoreBreakdown(
    int BaseSignalScore,
    int FrequencyScore,
    int TotalScore,
    bool ExplicitMaliciousVerdict,
    IReadOnlyList<DefenseScoreContribution> Contributions,
    IReadOnlyList<DefenseAdapterVerdict>? AdapterVerdicts = null,
    DefenseRoutingDetails? Routing = null,
    DefenseContainmentDetails? Containment = null)
{
    public IReadOnlyList<string> ContributorNames => Contributions
        .Select(contribution => contribution.Source)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

public sealed record DefenseScoreContribution(
    string Source,
    int ScoreDelta,
    IReadOnlyList<string> Signals,
    string Summary);

public sealed record DefenseAdapterVerdict(
    string Adapter,
    string Route,
    string Classification,
    bool? IsBot,
    int ScoreDelta,
    bool Decisive,
    IReadOnlyList<string> Signals,
    string Summary);

public sealed record DefenseRoutingDetails(
    string PrimaryRoute,
    string EffectiveRoute,
    bool FallbackEnabled,
    IReadOnlyList<string> OrderedAdapters,
    IReadOnlyList<string> EvaluatedAdapters);

public sealed record DefenseContainmentDetails(
    string Action,
    string Reason,
    bool ShouldBlock);

public sealed record DefenseEventMetrics(
    long TotalDecisions,
    long BlockedCount,
    long ObservedCount,
    DateTimeOffset? LatestDecisionAtUtc);

public sealed record OperatorRecommendation(
    string Id,
    string Category,
    string Severity,
    string Title,
    string Summary,
    string Rationale,
    string CurrentValue,
    string SuggestedValue,
    IReadOnlyList<string> Evidence);

public sealed record OperatorRecommendationSnapshot(
    DateTimeOffset GeneratedAtUtc,
    int RecentDecisionCount,
    IReadOnlyList<OperatorRecommendation> Recommendations);

public sealed record DefenseDecisionFeedback(
    long Id,
    long DecisionId,
    string IpAddress,
    string OriginalAction,
    string UpdatedAction,
    string Reason,
    string Actor,
    DateTimeOffset CreatedAtUtc);

public sealed record RequestSignalEvaluation(
    bool BlockImmediately,
    string BlockReason,
    IReadOnlyList<string> Signals);

public sealed record IntakeWebhookEvent(
    [property: JsonPropertyName("event_type")] string EventType,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("timestamp_utc")] DateTimeOffset TimestampUtc,
    [property: JsonPropertyName("details")] IntakeWebhookDetails Details);

public sealed record IntakeWebhookDetails(
    [property: JsonPropertyName("ip")] string IpAddress,
    [property: JsonPropertyName("method")] string? Method,
    [property: JsonPropertyName("path")] string? Path,
    [property: JsonPropertyName("query_string")] string? QueryString,
    [property: JsonPropertyName("user_agent")] string? UserAgent,
    [property: JsonPropertyName("signals")] IReadOnlyList<string>? Signals);

public sealed record WebhookInboxItem(
    long Id,
    IntakeWebhookEvent Event);

public sealed record IntakeDeliveryRecord(
    string DeliveryType,
    string Channel,
    string IpAddress,
    string Reason,
    string Target,
    string Status,
    string Detail,
    DateTimeOffset AttemptedAtUtc);

public sealed record IntakeDeliveryMetrics(
    long TotalAttempts,
    long SucceededCount,
    long FailedCount,
    long SkippedCount,
    DateTimeOffset? LatestAttemptAtUtc);

public sealed record CommunityBlocklistSyncStatus(
    bool Enabled,
    DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset? LastSuccessAtUtc,
    int ImportedCount,
    int RejectedCount,
    string? LastError,
    IReadOnlyList<CommunityBlocklistSourceSyncStatus> Sources);

public sealed record CommunityBlocklistSourceSyncStatus(
    string Name,
    string Url,
    int ImportedCount,
    int RejectedCount,
    DateTimeOffset? LastSuccessAtUtc,
    string? LastError);

public sealed record PeerDefenseSignal(
    string IpAddress,
    string Summary,
    IReadOnlyList<string> Signals,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset DecidedAtUtc);

public sealed record PeerDefenseSignalEnvelope(
    string Source,
    IReadOnlyList<PeerDefenseSignal> Signals);

public sealed record PeerSyncStatus(
    bool Enabled,
    DateTimeOffset? LastAttemptAtUtc,
    DateTimeOffset? LastSuccessAtUtc,
    int ImportedCount,
    int BlockedCount,
    int ObservedCount,
    int RejectedCount,
    string? LastError,
    IReadOnlyList<PeerSyncPeerStatus> Peers);

public sealed record PeerSyncPeerStatus(
    string Name,
    string Url,
    string TrustMode,
    int ImportedCount,
    int BlockedCount,
    int ObservedCount,
    int RejectedCount,
    DateTimeOffset? LastSuccessAtUtc,
    string? LastError);
