using System.Text.Json;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class RedisBlocklistService : IBlocklistService
{
    private readonly IRedisConnectionProvider _redisConnectionProvider;
    private readonly RedisOptions _options;

    public RedisBlocklistService(
        IRedisConnectionProvider redisConnectionProvider,
        IOptions<DefenseEngineOptions> options)
    {
        _redisConnectionProvider = redisConnectionProvider;
        _options = options.Value.Redis;
    }

    public async Task<bool> IsBlockedAsync(string ipAddress, CancellationToken cancellationToken)
    {
        var redis = await _redisConnectionProvider.GetAsync(cancellationToken);
        var database = redis.GetDatabase(_options.BlocklistDatabase);
        return await database.KeyExistsAsync(GetBlocklistKey(ipAddress));
    }

    public async Task BlockAsync(
        string ipAddress,
        string reason,
        IReadOnlyCollection<string> signals,
        CancellationToken cancellationToken)
    {
        var redis = await _redisConnectionProvider.GetAsync(cancellationToken);
        var database = redis.GetDatabase(_options.BlocklistDatabase);
        var payload = JsonSerializer.Serialize(new
        {
            reason,
            signals,
            blockedAtUtc = DateTimeOffset.UtcNow
        });

        await database.StringSetAsync(
            GetBlocklistKey(ipAddress),
            payload,
            TimeSpan.FromMinutes(Math.Max(1, _options.BlockDurationMinutes)));
    }

    public async Task UnblockAsync(string ipAddress, CancellationToken cancellationToken)
    {
        var redis = await _redisConnectionProvider.GetAsync(cancellationToken);
        var database = redis.GetDatabase(_options.BlocklistDatabase);
        await database.KeyDeleteAsync(GetBlocklistKey(ipAddress));
    }

    private string GetBlocklistKey(string ipAddress)
    {
        return $"{_options.BlocklistKeyPrefix}{ipAddress}";
    }
}
