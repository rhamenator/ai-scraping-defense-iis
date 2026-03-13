using System.Collections.Concurrent;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class DefenseEventStore : IDefenseEventStore
{
    private readonly ConcurrentQueue<DefenseDecision> _events = new();
    private const int MaxEvents = 200;

    public void Add(DefenseDecision decision)
    {
        _events.Enqueue(decision);

        while (_events.Count > MaxEvents && _events.TryDequeue(out _))
        {
        }
    }

    public IReadOnlyList<DefenseDecision> GetRecent(int count)
    {
        var safeCount = Math.Clamp(count, 1, MaxEvents);
        return _events.Reverse().Take(safeCount).ToArray();
    }
}
