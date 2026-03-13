using StackExchange.Redis;

namespace RedisBlocklistMiddlewareApp.Services;

public interface IRedisConnectionProvider
{
    Task<IConnectionMultiplexer> GetAsync(CancellationToken cancellationToken);
}
