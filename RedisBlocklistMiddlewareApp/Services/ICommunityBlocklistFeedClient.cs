using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public interface ICommunityBlocklistFeedClient
{
    Task<IReadOnlyList<string>> FetchAsync(
        CommunityBlocklistSourceOptions source,
        CancellationToken cancellationToken);
}
