namespace RedisBlocklistMiddlewareApp.Services;

public sealed record ConsensusCommand(
    Guid CommandId,
    string Operation,
    string IpAddress,
    string Reason,
    string[] Signals,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset? ExpiresAtUtc)
{
    public const string BlockOperation = "block";

    public const string UnblockOperation = "unblock";

    public static ConsensusCommand Block(
        string ipAddress,
        string reason,
        IReadOnlyCollection<string> signals,
        DateTimeOffset expiresAtUtc) =>
        new(
            Guid.NewGuid(),
            BlockOperation,
            ipAddress,
            reason,
            signals.ToArray(),
            DateTimeOffset.UtcNow,
            expiresAtUtc);

    public static ConsensusCommand Unblock(string ipAddress) =>
        new(
            Guid.NewGuid(),
            UnblockOperation,
            ipAddress,
            "manual_unblock",
            [],
            DateTimeOffset.UtcNow,
            null);
}

public sealed record ConsensusMutationResponse(bool Committed, string? Error = null);
