using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class RedisBlocklistService : IBlocklistService
{
    private readonly IRedisConnectionProvider _redisConnectionProvider;
    private readonly RedisOptions _options;
    private readonly NetworkingOptions _networkingOptions;
    private readonly ILogger<RedisBlocklistService> _logger;

    public RedisBlocklistService(
        IRedisConnectionProvider redisConnectionProvider,
        IOptions<DefenseEngineOptions> options,
        ILogger<RedisBlocklistService> logger)
    {
        _redisConnectionProvider = redisConnectionProvider;
        _options = options.Value.Redis;
        _networkingOptions = options.Value.Networking;
        _logger = logger;
    }

    public async Task<bool> IsBlockedAsync(string ipAddress, CancellationToken cancellationToken)
    {
        var redis = await _redisConnectionProvider.GetAsync(cancellationToken);
        var database = redis.GetDatabase(_options.BlocklistDatabase);
        return await database.KeyExistsAsync(GetBlocklistKey(ipAddress));
    }

    public async Task<bool> BlockAsync(
        string ipAddress,
        string reason,
        IReadOnlyCollection<string> signals,
        CancellationToken cancellationToken)
    {
        if (IsTrustedInfrastructureAddress(ipAddress))
        {
            _logger.LogWarning(
                "Refusing to block configured trusted proxy or CDN address {IpAddress}.",
                ipAddress);
            return false;
        }

        var redis = await _redisConnectionProvider.GetAsync(cancellationToken);
        var database = redis.GetDatabase(_options.BlocklistDatabase);
        var payload = JsonSerializer.Serialize(new
        {
            reason,
            signals,
            blockedAtUtc = DateTimeOffset.UtcNow
        });

        return await database.StringSetAsync(
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

    private bool IsTrustedInfrastructureAddress(string ipAddress)
    {
        return _networkingOptions.TrustedProxies.Any(entry =>
            string.Equals(entry, ipAddress, StringComparison.OrdinalIgnoreCase) ||
            (entry.Contains('/') && CidrMatcher.Contains(entry, ipAddress)));
    }
}
