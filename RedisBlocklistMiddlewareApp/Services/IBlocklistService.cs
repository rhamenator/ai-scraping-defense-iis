namespace RedisBlocklistMiddlewareApp.Services;

public interface IBlocklistService
{
    Task<bool> IsBlockedAsync(string ipAddress, CancellationToken cancellationToken);

    Task<bool> BlockAsync(
        string ipAddress,
        string reason,
        IReadOnlyCollection<string> signals,
        CancellationToken cancellationToken);

    /// <summary>Blocks until a fixed expiry (for example a peer signal's original expiry).</summary>
    Task<bool> BlockUntilAsync(
        string ipAddress,
        string reason,
        IReadOnlyCollection<string> signals,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken) =>
        BlockAsync(ipAddress, reason, signals, cancellationToken);

    Task UnblockAsync(string ipAddress, CancellationToken cancellationToken);
}
