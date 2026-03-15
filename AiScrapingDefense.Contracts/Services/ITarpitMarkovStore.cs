namespace RedisBlocklistMiddlewareApp.Services;

public interface ITarpitMarkovStore
{
    TarpitMarkovSnapshot? GetSnapshot();
}

public sealed record TarpitMarkovSnapshot(
    IReadOnlyDictionary<string, string[]> Transitions,
    IReadOnlyList<string> AvailableWords);
