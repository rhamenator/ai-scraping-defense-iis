using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using StackExchange.Redis;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class RedisConnectionProvider : IRedisConnectionProvider
{
    private readonly ILogger<RedisConnectionProvider> _logger;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private Task<IConnectionMultiplexer>? _connectionTask;

    public RedisConnectionProvider(
        IOptions<DefenseEngineOptions> options,
        ILogger<RedisConnectionProvider> logger)
    {
        _logger = logger;
        _connectionString = options.Value.Redis.ConnectionString;
    }

    public async Task<IConnectionMultiplexer> GetAsync(CancellationToken cancellationToken)
    {
        if (_connectionTask is not null)
        {
            return await _connectionTask.WaitAsync(cancellationToken);
        }

        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_connectionTask is null)
            {
                if (string.IsNullOrWhiteSpace(_connectionString))
                {
                    throw new InvalidOperationException("Redis connection string is not configured.");
                }

                _logger.LogInformation(
                    "Connecting to Redis at {RedisHost}",
                    _connectionString.Split(',')[0]);

                _connectionTask = CreateConnectionAsync();
            }
        }
        finally
        {
            _lock.Release();
        }

        return await _connectionTask.WaitAsync(cancellationToken);
    }

    private async Task<IConnectionMultiplexer> CreateConnectionAsync()
    {
        return await ConnectionMultiplexer.ConnectAsync(_connectionString);
    }
}
