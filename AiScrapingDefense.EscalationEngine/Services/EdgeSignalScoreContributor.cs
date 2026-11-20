using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class EdgeSignalScoreContributor : IThreatScoreContributor
{
    public string Name => "edge_signals";

    public int Order => 0;

    public Task<ThreatScoreContributorResult?> ContributeAsync(
        ThreatScoreContributorContext context,
        CancellationToken cancellationToken)
    {
        var score = ScoreSignals(context.Request.Signals);
        if (score <= 0)
        {
            return Task.FromResult<ThreatScoreContributorResult?>(null);
        }

        return Task.FromResult<ThreatScoreContributorResult?>(new ThreatScoreContributorResult(
            Name,
            score,
            context.Request.Signals,
            "Base edge heuristics contributed to the escalation score.",
            ThreatScoreContributionKind.BaseSignal));
    }

    private static int ScoreSignals(IReadOnlyList<string> signals)
    {
        // curl, wget and python-requests always omit Accept-Language and send Accept: */*, so those
        // header signals only restate that the client is a generic tool. Counting them on top of
        // the generic client signal (30 + 15 + 15) would block every such client on its own.
        var genericClient = signals.Any(signal => signal.StartsWith("generic_http_client:", StringComparison.Ordinal));
        return signals.Sum(signal => PointsFor(signal, genericClient));
    }

    private static int PointsFor(string signal, bool genericClient)
    {
        if (signal.StartsWith("known_bad_user_agent:", StringComparison.Ordinal) ||
            signal.StartsWith("robots_violation:", StringComparison.Ordinal))
        {
            return 100;
        }

        if (signal.StartsWith("generic_http_client:", StringComparison.Ordinal) ||
            signal.StartsWith("high_request_rate:", StringComparison.Ordinal) ||
            signal.StartsWith("suspicious_path:", StringComparison.Ordinal))
        {
            return 30;
        }

        return signal switch
        {
            "empty_user_agent" => 25,
            "missing_accept_language" or "generic_accept_any" => genericClient ? 0 : 15,
            "long_query_string" => 10,
            _ => 0
        };
    }
}
