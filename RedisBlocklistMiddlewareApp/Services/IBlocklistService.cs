namespace RedisBlocklistMiddlewareApp.Services;

public interface IBlocklistService
{
    Task<bool> IsBlockedAsync(string ipAddress, CancellationToken cancellationToken);

    Task<bool> BlockAsync(
        string ipAddress,
        string reason,
        IReadOnlyCollection<string> signals,
        CancellationToken cancellationToken);

    Task UnblockAsync(string ipAddress, CancellationToken cancellationToken);
}
