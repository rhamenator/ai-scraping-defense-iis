using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public interface IPeerSignalFeedClient
{
    Task<PeerDefenseSignalEnvelope> FetchAsync(
        PeerSyncPeerOptions peer,
        CancellationToken cancellationToken);
}
