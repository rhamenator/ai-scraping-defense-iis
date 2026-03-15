namespace RedisBlocklistMiddlewareApp.Services;

public interface IRequestFrequencyTracker
{
    Task<long> IncrementAsync(string ipAddress, CancellationToken cancellationToken);
}
