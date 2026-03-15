using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public interface IPeerSyncStatusStore
{
    PeerSyncStatus GetStatus();

    void Update(PeerSyncStatus status);
}
