using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public interface IThreatAssessmentService
{
    Task<ThreatAssessmentResult> AssessAsync(SuspiciousRequest request, CancellationToken cancellationToken);
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

public sealed record ThreatAssessmentResult(
    bool ShouldBlock,
    string BlockReason,
    string Summary,
    int Score,
    long Frequency,
    IReadOnlyList<string> Signals,
    DefenseScoreBreakdown Breakdown);
