using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class CommunityBlocklistSyncStatusStore : ICommunityBlocklistSyncStatusStore
{
    private readonly object _gate = new();
    private CommunityBlocklistSyncStatus _status;

    public CommunityBlocklistSyncStatusStore(IOptions<DefenseEngineOptions> options)
    {
        _status = new CommunityBlocklistSyncStatus(
            options.Value.CommunityBlocklist.Enabled,
            null,
            null,
            0,
            0,
            null,
            []);
    }

    public CommunityBlocklistSyncStatus GetStatus()
    {
        lock (_gate)
        {
            return _status;
        }
    }

    public void Update(CommunityBlocklistSyncStatus status)
    {
        lock (_gate)
        {
            _status = status;
        }
    }
}
