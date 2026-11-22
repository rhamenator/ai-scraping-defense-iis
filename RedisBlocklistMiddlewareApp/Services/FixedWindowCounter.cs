namespace RedisBlocklistMiddlewareApp.Services;

/// <summary>
/// A small in-memory fixed-window counter: at most <c>limit</c> events per key per window. Memory is bounded: when many
/// distinct keys have been seen, expired windows are dropped first.
/// </summary>
public sealed class FixedWindowCounter
{
    private const int MaximumKeys = 50_000;

    private readonly object _gate = new();
    private readonly Dictionary<string, (long Window, int Count)> _counts = new(StringComparer.Ordinal);
    private readonly Func<DateTimeOffset> _clock;

    public FixedWindowCounter(Func<DateTimeOffset>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>Counts one event for <paramref name="key"/>. False, with the time to wait, once the window's limit is passed.</summary>
    public bool TryAcquire(string key, int limit, TimeSpan window, out TimeSpan retryAfter)
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
            if (_counts.Count >= MaximumKeys)
            {
                Purge(windowId);
            }

            var count = _counts.TryGetValue(key, out var existing) && existing.Window == windowId ? existing.Count : 0;
            if (count >= limit)
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

        if (_counts.Count >= MaximumKeys)
        {
            _counts.Clear(); // a flood of distinct keys within one window: start over rather than grow
        }
    }
}
