using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public interface ICommunityBlocklistSyncStatusStore
{
    CommunityBlocklistSyncStatus GetStatus();

    void Update(CommunityBlocklistSyncStatus status);
}
