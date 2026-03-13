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
    DefenseScoreBreakdown? Breakdown = null);

public sealed record DefenseScoreBreakdown(
    int BaseSignalScore,
    int FrequencyScore,
    int TotalScore,
    bool ExplicitMaliciousVerdict,
    IReadOnlyList<DefenseScoreContribution> Contributions);

public sealed record DefenseScoreContribution(
    string Source,
    int ScoreDelta,
    IReadOnlyList<string> Signals,
    string Summary);

public sealed record DefenseEventMetrics(
    long TotalDecisions,
    long BlockedCount,
    long ObservedCount,
    DateTimeOffset? LatestDecisionAtUtc);

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
