namespace RedisBlocklistMiddlewareApp.Services;

/// <summary>
/// Counts every inspected request per client address inside the configured window.
/// This is separate from the suspicious-event counter used by the escalation engine.
/// </summary>
public interface IRequestRateTracker
{
    Task<long> IncrementAsync(string ipAddress, CancellationToken cancellationToken);
}
