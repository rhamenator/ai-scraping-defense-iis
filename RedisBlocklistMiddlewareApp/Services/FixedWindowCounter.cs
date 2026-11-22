namespace RedisBlocklistMiddlewareApp.Services;

/// <summary>
/// A small in-memory fixed-window counter: at most <c>limit</c> events per key per window. Memory is bounded: when many
/// distinct keys have been seen, expired windows are dropped first; if the table is still full, a NEW key is refused (fail
/// closed) and existing keys, with their limits and lockouts, are left alone. A flood of distinct keys therefore cannot reset
/// anyone's limit.
/// </summary>
public sealed class FixedWindowCounter
{
    private const int MaximumKeys = 50_000;

    private readonly object _gate = new();
    private readonly Dictionary<string, (long Window, int Count)> _counts = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;
    private long _lastPurgeWindow = -1;

    public FixedWindowCounter(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Counts one event for <paramref name="key"/>. False, with the time to wait, once the window's limit is passed. <paramref name="exemptFromCap"/> is for the few fixed keys that must always be tracked.</summary>
    public bool TryAcquire(string key, int limit, TimeSpan window, out TimeSpan retryAfter, bool exemptFromCap = false)
    {
        retryAfter = TimeSpan.Zero;
        if (limit <= 0)
        {
            return true;
        }

        var now = _clock();
        var windowId = now.UtcTicks / window.Ticks;
        lock (_gate)
        {
            if (_counts.Count >= MaximumKeys && _lastPurgeWindow != windowId)
            {
                // Once per window is enough (nothing new can have expired within it), and keeps a flood from costing O(n) per request.
                _lastPurgeWindow = windowId;
                Purge(windowId);
            }

            var known = _counts.TryGetValue(key, out var existing);
            var count = known && existing.Window == windowId ? existing.Count : 0;
            var full = !known && !exemptFromCap && _counts.Count >= MaximumKeys;
            if (full || count >= limit)
            {
                var windowEnd = new DateTimeOffset((windowId + 1) * window.Ticks, TimeSpan.Zero);
                retryAfter = windowEnd - now;
                return false;
            }

            _counts[key] = (windowId, count + 1);
            return true;
        }
    }

    /// <summary>Whether <paramref name="key"/> has already used its <paramref name="limit"/> in the current window (nothing is counted).</summary>
    public bool IsExhausted(string key, int limit, TimeSpan window, out TimeSpan retryAfter)
    {
        retryAfter = TimeSpan.Zero;
        if (limit <= 0)
        {
            return false;
        }

        var now = _clock();
        var windowId = now.UtcTicks / window.Ticks;
        lock (_gate)
        {
            if (!_counts.TryGetValue(key, out var existing) || existing.Window != windowId || existing.Count < limit)
            {
                return false;
            }

            retryAfter = new DateTimeOffset((windowId + 1) * window.Ticks, TimeSpan.Zero) - now;
            return true;
        }
    }

    internal int TrackedKeys
    {
        get
        {
            lock (_gate)
            {
                return _counts.Count;
            }
        }
    }

    private void Purge(long currentWindow)
    {
        foreach (var key in _counts.Where(entry => entry.Value.Window != currentWindow).Select(entry => entry.Key).ToArray())
        {
            _counts.Remove(key);
        }

    }
}
