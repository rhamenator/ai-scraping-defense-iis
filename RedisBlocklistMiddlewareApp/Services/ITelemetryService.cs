using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public interface ITelemetryService
{
    Task<TelemetrySnapshotResponse> RefreshTelemetryAsync(CancellationToken cancellationToken = default);
    Task<TelemetrySnapshotResponse?> GetCachedTelemetryAsync(CancellationToken cancellationToken = default);
}
