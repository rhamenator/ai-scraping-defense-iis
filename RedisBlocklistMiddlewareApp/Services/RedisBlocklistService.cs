using System.Text.Json;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using StackExchange.Redis;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class RedisBlocklistService : IBlocklistService
{
    private readonly IConnectionMultiplexer _redis;
    private readonly RedisOptions _options;

    public RedisBlocklistService(
        IConnectionMultiplexer redis,
        IOptions<DefenseEngineOptions> options)
    {
        _redis = redis;
        _options = options.Value.Redis;
    }

    public Task<bool> IsBlockedAsync(string ipAddress, CancellationToken cancellationToken)
    {
        var database = _redis.GetDatabase(_options.BlocklistDatabase);
        return database.KeyExistsAsync(GetBlocklistKey(ipAddress));
    }

    public async Task BlockAsync(
        string ipAddress,
        string reason,
        IReadOnlyCollection<string> signals,
        CancellationToken cancellationToken)
    {
        var database = _redis.GetDatabase(_options.BlocklistDatabase);
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

    private string GetBlocklistKey(string ipAddress)
    {
        return $"{_options.BlocklistKeyPrefix}{ipAddress}";
    }
}
