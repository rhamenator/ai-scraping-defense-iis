using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public interface ISuspiciousRequestQueue
{
    ValueTask<bool> QueueAsync(
        SuspiciousRequest request,
        CancellationToken cancellationToken);

    IAsyncEnumerable<SuspiciousRequest> ReadAllAsync(CancellationToken cancellationToken);
}
