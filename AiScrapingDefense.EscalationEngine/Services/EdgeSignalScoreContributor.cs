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
        var score = 0;

        foreach (var signal in signals)
        {
            if (signal.StartsWith("known_bad_user_agent:", StringComparison.Ordinal))
            {
                score += 100;
            }
            else if (signal.StartsWith("suspicious_path:", StringComparison.Ordinal))
            {
                score += 30;
            }
            else if (string.Equals(signal, "empty_user_agent", StringComparison.Ordinal))
            {
                score += 25;
            }
            else if (string.Equals(signal, "missing_accept_language", StringComparison.Ordinal))
            {
                score += 15;
            }
            else if (string.Equals(signal, "generic_accept_any", StringComparison.Ordinal))
            {
                score += 15;
            }
            else if (string.Equals(signal, "long_query_string", StringComparison.Ordinal))
            {
                score += 10;
            }
        }

        return score;
    }
}