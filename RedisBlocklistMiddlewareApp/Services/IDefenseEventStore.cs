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

    /// <summary>
    /// Prunes the oldest <paramref name="fraction"/> of stored decision rows (at least <paramref name="minimumRows"/>)
    /// while keeping decisions referenced by operator feedback.
    /// </summary>
    int PruneOldest(double fraction, int minimumRows) => 0;
}
