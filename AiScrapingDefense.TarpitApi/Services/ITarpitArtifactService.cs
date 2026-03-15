namespace RedisBlocklistMiddlewareApp.Services;

public interface ITarpitArtifactService
{
    Task<TarpitArtifact?> TryGetArtifactAsync(string path, CancellationToken cancellationToken);
}

public sealed record TarpitArtifact(
    string FileName,
    string ContentType,
    byte[] Content);
