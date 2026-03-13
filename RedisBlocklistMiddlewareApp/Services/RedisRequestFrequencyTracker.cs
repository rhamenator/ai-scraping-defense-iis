using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using StackExchange.Redis;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class RedisRequestFrequencyTracker : IRequestFrequencyTracker
{
    private readonly IConnectionMultiplexer _redis;
    private readonly RedisOptions _options;

    public RedisRequestFrequencyTracker(
        IConnectionMultiplexer redis,
        IOptions<DefenseEngineOptions> options)
    {
        _redis = redis;
        _options = options.Value.Redis;
    }

    public async Task<long> IncrementAsync(string ipAddress, CancellationToken cancellationToken)
    {
        var database = _redis.GetDatabase(_options.FrequencyDatabase);
        var key = $"{_options.FrequencyKeyPrefix}{ipAddress}";
        var count = await database.StringIncrementAsync(key);

        if (count == 1)
        {
            await database.KeyExpireAsync(
                key,
                TimeSpan.FromSeconds(Math.Max(1, _options.FrequencyWindowSeconds)));
        }

        return count;
    }
}
