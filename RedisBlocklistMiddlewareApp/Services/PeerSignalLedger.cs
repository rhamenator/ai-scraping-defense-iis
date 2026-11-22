using System.Collections.Concurrent;

namespace RedisBlocklistMiddlewareApp.Services;

/// <summary>Remembers signal IDs already imported so a replayed or looped signal is applied only once.</summary>
public sealed class PeerSignalLedger
{
    private const int MaximumEntries = 20000;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);

    /// <summary>Returns true if the ID is new (and records it until <paramref name="expiresAtUtc"/>).</summary>
    public bool TryAdd(string signalId, DateTimeOffset expiresAtUtc)
    {
        Prune();
        if (_seen.Count >= MaximumEntries)
        {
            // Full of unexpired IDs: refuse new ones rather than grow without bound or forget old ones early.
            return false;
        }

        return _seen.TryAdd(signalId, expiresAtUtc);
    }

    private void Prune()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var signalId in _seen.Where(entry => entry.Value <= now).Select(entry => entry.Key))
        {
            _seen.TryRemove(signalId, out _);
        }
    }
}
