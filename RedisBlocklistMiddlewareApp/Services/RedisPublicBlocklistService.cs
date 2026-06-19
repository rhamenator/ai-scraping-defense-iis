using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using StackExchange.Redis;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class RedisPublicBlocklistService : IPublicBlocklistService
{
    private const string ServiceName = "ai-scraping-defense-dotnet";

    private readonly IRedisConnectionProvider _redisConnectionProvider;
    private readonly IBlocklistService _blocklistService;
    private readonly DefenseEngineOptions _options;
    private readonly ILogger<RedisPublicBlocklistService> _logger;

    public RedisPublicBlocklistService(
        IRedisConnectionProvider redisConnectionProvider,
        IBlocklistService blocklistService,
        IOptions<DefenseEngineOptions> options,
        ILogger<RedisPublicBlocklistService> logger)
    {
        _redisConnectionProvider = redisConnectionProvider;
        _blocklistService = blocklistService;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<PublicBlocklistEnvelope> ListAsync(
        int count,
        CancellationToken cancellationToken)
    {
        var safeCount = Math.Clamp(
            count <= 0 ? _options.PublicBlocklist.MaximumListEntries : count,
            1,
            _options.PublicBlocklist.MaximumListEntries);

        var redis = await _redisConnectionProvider.GetAsync(cancellationToken);
        var endpoints = redis.GetEndPoints();
        if (endpoints.Length == 0)
        {
            return new PublicBlocklistEnvelope(ServiceName, []);
        }

        var server = endpoints
            .Select(endpoint => redis.GetServer(endpoint))
            .FirstOrDefault(candidate => candidate.IsConnected);

        if (server is null)
        {
            _logger.LogWarning("No connected Redis server was available for public blocklist scanning.");
            return new PublicBlocklistEnvelope(ServiceName, []);
        }

        var prefix = _options.Redis.BlocklistKeyPrefix;
        var entries = server
            .Keys(
                database: _options.Redis.BlocklistDatabase,
                pattern: prefix + "*",
                pageSize: safeCount)
            .Take(safeCount)
            .Select(key => ToPublicBlocklistEntry(key, prefix))
            .Where(entry => !string.IsNullOrWhiteSpace(entry.IpAddress))
            .ToArray();

        return new PublicBlocklistEnvelope(ServiceName, entries);
    }

    public async Task<PublicBlocklistReportResponse> ReportAsync(
        string ipAddress,
        string reason,
        string source,
        CancellationToken cancellationToken)
    {
        var normalizedReason = string.IsNullOrWhiteSpace(reason)
            ? _options.PublicBlocklist.ReportReason
            : reason.Trim();
        var normalizedSource = string.IsNullOrWhiteSpace(source)
            ? "public_blocklist"
            : source.Trim();

        await _blocklistService.BlockAsync(
            ipAddress,
            normalizedReason,
            [normalizedSource, "public_blocklist_report"],
            cancellationToken);

        return new PublicBlocklistReportResponse(
            ipAddress,
            Blocked: true,
            normalizedReason);
    }

    private static PublicBlocklistEntry ToPublicBlocklistEntry(RedisKey key, string prefix)
    {
        var value = key.ToString();
        var ipAddress = value.StartsWith(prefix, StringComparison.Ordinal)
            ? value[prefix.Length..]
            : value;

        return new PublicBlocklistEntry(ipAddress, ServiceName);
    }
}
