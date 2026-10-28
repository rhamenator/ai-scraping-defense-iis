using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed record DistributedAttackSignal(
    DateTimeOffset WindowStartUtc,
    DateTimeOffset DetectedAtUtc,
    int DecisionCount,
    int BlockedCount,
    int DistinctBlockedOrigins,
    double BlockedRate);

public static class DistributedAttackSignalEvaluator
{
    public static DistributedAttackSignal? Evaluate(
        IReadOnlyList<DefenseDecision> decisions,
        CloudflareOptions options,
        DateTimeOffset now)
    {
        var windowStart = now.AddMinutes(-options.AttackWindowMinutes);
        var recent = decisions
            .Where(decision => decision.DecidedAtUtc >= windowStart &&
                               decision.DecidedAtUtc <= now.AddMinutes(1))
            .ToArray();
        if (recent.Length < options.MinimumRecentDecisions)
        {
            return null;
        }

        var blocked = recent
            .Where(decision => string.Equals(decision.Action, "blocked", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var blockedRate = blocked.Length / (double)recent.Length;
        var distinctOrigins = blocked
            .Select(decision => decision.IpAddress)
            .Where(ipAddress => !string.IsNullOrWhiteSpace(ipAddress))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        if (blockedRate < options.MinimumBlockedRate ||
            distinctOrigins < options.MinimumDistinctBlockedOrigins)
        {
            return null;
        }

        return new DistributedAttackSignal(
            windowStart,
            now,
            recent.Length,
            blocked.Length,
            distinctOrigins,
            blockedRate);
    }
}
