using System.Text.Json;
using System.Net;
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
        if (!TryNormalizeIpAddress(ipAddress, out var normalizedIp))
        {
            return false;
        }
        var redis = await _redisConnectionProvider.GetAsync(cancellationToken);
        var database = redis.GetDatabase(_options.BlocklistDatabase);
        return await database.KeyExistsAsync(GetBlocklistKey(normalizedIp));
    }

    public async Task<bool> BlockAsync(
        string ipAddress,
        string reason,
        IReadOnlyCollection<string> signals,
        CancellationToken cancellationToken)
    {
        return await BlockUntilAsync(
            ipAddress,
            reason,
            signals,
            DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, _options.BlockDurationMinutes)),
            cancellationToken);
    }

    internal DateTimeOffset GetBlockExpirationUtc() =>
        DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, _options.BlockDurationMinutes));

    internal bool CanBlock(string ipAddress) =>
        TryNormalizeIpAddress(ipAddress, out var normalizedIp) &&
        !IsTrustedInfrastructureAddress(normalizedIp);

    internal async Task<bool> BlockUntilAsync(
        string ipAddress,
        string reason,
        IReadOnlyCollection<string> signals,
        DateTimeOffset expiresAtUtc,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeIpAddress(ipAddress, out var normalizedIp) || !CanBlock(normalizedIp))
        {
            _logger.LogWarning(
                "Refusing to block configured trusted proxy or CDN address {IpAddress}.",
                ipAddress);
            return false;
        }

        var remaining = expiresAtUtc - DateTimeOffset.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            _logger.LogDebug(
                "Skipped expired block command for {IpAddress}; it expired at {ExpiresAtUtc}.",
                ipAddress,
                expiresAtUtc);
            return true;
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
            GetBlocklistKey(normalizedIp),
            payload,
            remaining);
    }

    public async Task UnblockAsync(string ipAddress, CancellationToken cancellationToken)
    {
        if (!TryNormalizeIpAddress(ipAddress, out var normalizedIp))
        {
            return;
        }
        var redis = await _redisConnectionProvider.GetAsync(cancellationToken);
        var database = redis.GetDatabase(_options.BlocklistDatabase);
        await database.KeyDeleteAsync(GetBlocklistKey(normalizedIp));
    }

    private string GetBlocklistKey(string ipAddress)
    {
        return $"{_options.BlocklistKeyPrefix}{ipAddress}";
    }

    internal static bool TryNormalizeIpAddress(string value, out string normalizedIp)
    {
        normalizedIp = string.Empty;
        if (!IPAddress.TryParse(value, out var address))
        {
            return false;
        }
        normalizedIp = (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
        return true;
    }

    private bool IsTrustedInfrastructureAddress(string ipAddress)
    {
        return _networkingOptions.TrustedProxies
            .Concat(_networkingOptions.TrustedCdnProxies)
            .Any(entry =>
            string.Equals(entry, ipAddress, StringComparison.OrdinalIgnoreCase) ||
            (entry.Contains('/') && CidrMatcher.Contains(entry, ipAddress)));
    }
}
