using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class ConfiguredRangeReputationProvider : IThreatReputationProvider
{
    private readonly ConfiguredRangeReputationOptions _options;

    public ConfiguredRangeReputationProvider(IOptions<DefenseEngineOptions> options)
    {
        _options = options.Value.Escalation.ConfiguredRanges;
    }

    public string Name => "configured_ranges";

    public Task<ReputationAssessment?> AssessAsync(
        ThreatAssessmentContext context,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled || _options.Entries.Length == 0)
        {
            return Task.FromResult<ReputationAssessment?>(null);
        }

        var matchingEntries = _options.Entries
            .Where(entry =>
                !string.IsNullOrWhiteSpace(entry.Cidr) &&
                CidrMatcher.Contains(entry.Cidr, context.IpAddress))
            .ToArray();

        if (matchingEntries.Length == 0)
        {
            return Task.FromResult<ReputationAssessment?>(null);
        }

        var signals = matchingEntries
            .SelectMany(entry =>
            {
                if (entry.Signals.Length > 0)
                {
                    return entry.Signals;
                }

                var signalName = string.IsNullOrWhiteSpace(entry.Name)
                    ? entry.Cidr
                    : entry.Name.Trim().Replace(" ", "_", StringComparison.Ordinal);
                return new[] { $"reputation_range:{signalName}" };
            })
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var scoreAdjustment = matchingEntries.Sum(entry => entry.ScoreAdjustment);
        var summary = "Matched configured reputation range(s): " +
            string.Join(", ", matchingEntries.Select(entry => string.IsNullOrWhiteSpace(entry.Name) ? entry.Cidr : entry.Name.Trim()));

        return Task.FromResult<ReputationAssessment?>(new ReputationAssessment(
            Name,
            scoreAdjustment,
            scoreAdjustment > 0,
            signals,
            summary));
    }
}
