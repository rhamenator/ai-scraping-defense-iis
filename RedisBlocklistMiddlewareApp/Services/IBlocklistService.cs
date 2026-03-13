namespace RedisBlocklistMiddlewareApp.Services;

public interface IBlocklistService
{
    Task<bool> IsBlockedAsync(string ipAddress, CancellationToken cancellationToken);

    Task BlockAsync(
        string ipAddress,
        string reason,
        IReadOnlyCollection<string> signals,
        CancellationToken cancellationToken);
}
