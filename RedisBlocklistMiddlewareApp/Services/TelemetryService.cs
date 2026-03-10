using Microsoft.Extensions.DependencyInjection;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public class TelemetryService : ITelemetryService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TelemetryService> _logger;
    private readonly SemaphoreSlim _cacheLock = new(1, 1);
    private TelemetrySnapshotResponse? _cachedTelemetry;

    public TelemetryService(IServiceScopeFactory scopeFactory, ILogger<TelemetryService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<TelemetrySnapshotResponse> RefreshTelemetryAsync(CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IDefenseEngineClient>();
        var telemetry = await client.GetTelemetryAsync(cancellationToken);

        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            _cachedTelemetry = telemetry;
        }
        finally
        {
            _cacheLock.Release();
        }

        _logger.LogInformation("Telemetry cache refreshed with {Count} events at {Timestamp}.", telemetry.EventCount, telemetry.RetrievedAt);
        return telemetry;
    }

    public async Task<TelemetrySnapshotResponse?> GetCachedTelemetryAsync(CancellationToken cancellationToken = default)
    {
        await _cacheLock.WaitAsync(cancellationToken);
        try
        {
            return _cachedTelemetry;
        }
        finally
        {
            _cacheLock.Release();
        }
    }
}
