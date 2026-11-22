using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RedisBlocklistMiddlewareApp.Configuration;
using StackExchange.Redis;

namespace RedisBlocklistMiddlewareApp.Services;

/// <summary>Shared hop state for the edge and the dedicated public tarpit origin.</summary>
public sealed class TarpitHopLimiter : IAsyncDisposable
{
    private const string CountScript = "local n = redis.call('INCR', KEYS[1]); redis.call('EXPIRE', KEYS[1], ARGV[1]); return n";
    private const string FlagScript = "local n = redis.call('INCR', KEYS[1]); redis.call('EXPIRE', KEYS[1], ARGV[1]); if n > tonumber(ARGV[2]) then redis.call('SET', KEYS[2], ARGV[3]) else redis.call('SETEX', KEYS[2], ARGV[1], ARGV[3]) end; return n";
    private readonly DefenseEngineOptions _options;
    private readonly ILogger<TarpitHopLimiter> _logger;
    private readonly Lazy<Task<ConnectionMultiplexer>> _connection;

    public TarpitHopLimiter(DefenseEngineOptions options, ILogger<TarpitHopLimiter> logger)
    {
        _options = options;
        _logger = logger;
        _connection = new Lazy<Task<ConnectionMultiplexer>>(() => ConnectAsync(options.Redis.ConnectionString));
    }

    private static async Task<ConnectionMultiplexer> ConnectAsync(string connectionString)
    {
        var config = ConfigurationOptions.Parse(connectionString);
        config.AbortOnConnectFail = false;
        config.ConnectTimeout = Math.Min(config.ConnectTimeout, 500);
        config.SyncTimeout = Math.Min(config.SyncTimeout, 500);
        return await ConnectionMultiplexer.ConnectAsync(config);
    }

    private static string? Normalize(string ip) =>
        IPAddress.TryParse(ip, out var address)
            ? (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString()
            : null;

    private async Task<IDatabase> DatabaseAsync() =>
        (await _connection.Value).GetDatabase(_options.Redis.BlocklistDatabase);

    /// <returns>The count for an already flagged IP, or null when disabled, unflagged, or unavailable.</returns>
    public async Task<long?> CountFlaggedHopAsync(string ip)
    {
        var normalized = Normalize(ip);
        if (_options.Tarpit.MaxHops <= 0 || normalized is null)
            return null;
        try
        {
            var database = await DatabaseAsync();
            if (!await database.KeyExistsAsync($"ip_flag:{normalized}"))
                return null;
            var count = await database.ScriptEvaluateAsync(
                CountScript,
                [$"tarpit:hops:{normalized}"],
                [Math.Max(1, _options.Tarpit.HopWindowSeconds)]);
            return (long)count;
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException or ArgumentException)
        {
            _logger.LogWarning("Redis tarpit hop check failed for {IpAddress}; serving request", ip);
            return null;
        }
    }

    public async Task FlagAsync(string ip)
    {
        var normalized = Normalize(ip);
        if (normalized is null) return;
        try
        {
            await (await DatabaseAsync()).ScriptEvaluateAsync(
                FlagScript,
                [$"ip_flag_count:{normalized}", $"ip_flag:{normalized}"],
                [_options.Tarpit.FlaggedIpTtlSeconds, _options.Tarpit.RepeatOffenderThreshold, "Tarpit Hit"]);
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException or ArgumentException)
        {
            _logger.LogWarning("Redis tarpit flag write failed for {IpAddress}", ip);
        }
    }

    public async Task BlockAsync(string ip, string reason)
    {
        var normalized = Normalize(ip);
        if (normalized is null) return;
        var address = IPAddress.Parse(normalized);
        if (_options.Networking.TrustedProxies.Concat(_options.Networking.TrustedCdnProxies)
            .Any(entry => string.Equals(entry, normalized, StringComparison.OrdinalIgnoreCase) ||
                (IPNetwork.TryParse(entry, out var network) && network.Contains(address)))) return;
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                reason,
                signals = new[] { "tarpit_hop_limit" },
                blockedAtUtc = DateTimeOffset.UtcNow
            });
            var prefix = _options.Redis.BlocklistKeyPrefix.EndsWith(':')
                ? _options.Redis.BlocklistKeyPrefix
                : $"{_options.Redis.BlocklistKeyPrefix}:";
            await (await DatabaseAsync()).StringSetAsync(
                $"{prefix}{normalized}",
                payload,
                TimeSpan.FromMinutes(Math.Max(1, _options.Redis.BlockDurationMinutes)));
        }
        catch (Exception exception) when (exception is RedisException or TimeoutException or ArgumentException)
        {
            _logger.LogWarning("Redis tarpit block write failed for {IpAddress}", ip);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection.IsValueCreated)
        {
            try { (await _connection.Value).Dispose(); }
            catch (RedisException) { }
        }
    }
}
