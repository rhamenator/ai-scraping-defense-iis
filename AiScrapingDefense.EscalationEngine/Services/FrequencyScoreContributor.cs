namespace RedisBlocklistMiddlewareApp.Services;

public sealed class FrequencyScoreContributor : IThreatScoreContributor
{
    public string Name => "frequency";

    public int Order => 100;

    public Task<ThreatScoreContributorResult?> ContributeAsync(
        ThreatScoreContributorContext context,
        CancellationToken cancellationToken)
    {
        var score = (int)Math.Min(25, context.Frequency * 5);
        if (score <= 0)
        {
            return Task.FromResult<ThreatScoreContributorResult?>(null);
        }

        return Task.FromResult<ThreatScoreContributorResult?>(new ThreatScoreContributorResult(
            Name,
            score,
            ["frequency_window"],
            "Short-window suspicious request frequency increased the escalation score.",
            ThreatScoreContributionKind.Frequency));
    }
}