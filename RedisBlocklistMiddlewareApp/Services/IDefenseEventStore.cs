using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public interface IDefenseEventStore
{
    void Add(DefenseDecision decision);

    IReadOnlyList<DefenseDecision> GetRecent(int count);

    DefenseEventMetrics GetMetrics();
}
