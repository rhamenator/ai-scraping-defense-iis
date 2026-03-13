namespace RedisBlocklistMiddlewareApp.Models;

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
    DateTimeOffset DecidedAtUtc);

public sealed record RequestSignalEvaluation(
    bool BlockImmediately,
    string BlockReason,
    IReadOnlyList<string> Signals);
