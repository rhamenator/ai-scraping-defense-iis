using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public interface IDefenseEventStore
{
    void Add(DefenseDecision decision);

    IReadOnlyList<DefenseDecision> GetRecent(int count);

    DefenseDecision? GetById(long id);

    DefenseDecisionFeedback AddFeedback(DefenseDecisionFeedback feedback);

    IReadOnlyList<DefenseDecisionFeedback> GetRecentFeedback(int count);

    DefenseEventMetrics GetMetrics();
}
