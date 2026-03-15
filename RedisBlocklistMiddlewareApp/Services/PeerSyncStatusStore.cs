using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class PeerSyncStatusStore : IPeerSyncStatusStore
{
    private readonly object _gate = new();
    private PeerSyncStatus _status;

    public PeerSyncStatusStore(IOptions<DefenseEngineOptions> options)
    {
        _status = new PeerSyncStatus(
            options.Value.PeerSync.Enabled,
            null,
            null,
            0,
            0,
            0,
            0,
            null,
            []);
    }

    public PeerSyncStatus GetStatus()
    {
        lock (_gate)
        {
            return _status;
        }
    }

    public void Update(PeerSyncStatus status)
    {
        lock (_gate)
        {
            _status = status;
        }
    }
}
