using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using StackExchange.Redis;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class RedisRequestFrequencyTracker : IRequestFrequencyTracker
{
    private static readonly LuaScript IncrementAndExpireScript = LuaScript.Prepare(
        """
        local current = redis.call('INCR', @key)
        if redis.call('TTL', @key) < 0 then
            redis.call('EXPIRE', @key, @ttlSeconds)
        end
        return current
        """);

    private readonly IRedisConnectionProvider _redisConnectionProvider;
    private readonly RedisOptions _options;

    public RedisRequestFrequencyTracker(
        IRedisConnectionProvider redisConnectionProvider,
        IOptions<DefenseEngineOptions> options)
    {
        _redisConnectionProvider = redisConnectionProvider;
        _options = options.Value.Redis;
    }

    public async Task<long> IncrementAsync(string ipAddress, CancellationToken cancellationToken)
    {
        var redis = await _redisConnectionProvider.GetAsync(cancellationToken);
        var database = redis.GetDatabase(_options.FrequencyDatabase);
        var key = $"{_options.FrequencyKeyPrefix}{ipAddress}";
        var result = await database.ScriptEvaluateAsync(
            IncrementAndExpireScript,
            new
            {
                key = (RedisKey)key,
                ttlSeconds = Math.Max(1, _options.FrequencyWindowSeconds)
            });

        return (long)result;
    }
}
