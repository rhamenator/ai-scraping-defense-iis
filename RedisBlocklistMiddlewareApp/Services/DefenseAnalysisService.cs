using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class DefenseAnalysisService : BackgroundService
{
    private readonly ISuspiciousRequestQueue _queue;
    private readonly IRequestFrequencyTracker _frequencyTracker;
    private readonly IBlocklistService _blocklistService;
    private readonly IDefenseEventStore _eventStore;
    private readonly HeuristicOptions _options;
    private readonly ILogger<DefenseAnalysisService> _logger;

    public DefenseAnalysisService(
        ISuspiciousRequestQueue queue,
        IRequestFrequencyTracker frequencyTracker,
        IBlocklistService blocklistService,
        IDefenseEventStore eventStore,
        IOptions<DefenseEngineOptions> options,
        ILogger<DefenseAnalysisService> logger)
    {
        _queue = queue;
        _frequencyTracker = frequencyTracker;
        _blocklistService = blocklistService;
        _eventStore = eventStore;
        _options = options.Value.Heuristics;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                var frequency = await _frequencyTracker.IncrementAsync(
                    request.IpAddress,
                    stoppingToken);
                var score = ScoreSignals(request.Signals, frequency);
                var shouldBlock =
                    score >= _options.BlockScoreThreshold ||
                    frequency >= _options.FrequencyBlockThreshold;
                var action = shouldBlock ? "blocked" : "observed";
                var summary = shouldBlock
                    ? "Queued analysis crossed the automated block threshold."
                    : "Queued analysis recorded the request for continued observation.";

                if (shouldBlock)
                {
                    await _blocklistService.BlockAsync(
                        request.IpAddress,
                        "queued_analysis_threshold",
                        request.Signals,
                        stoppingToken);

                    _logger.LogWarning(
                        "Blocked IP {IpAddress} after queued analysis with score {Score} and frequency {Frequency}.",
                        request.IpAddress,
                        score,
                        frequency);
                }
                else
                {
                    _logger.LogInformation(
                        "Observed suspicious request from {IpAddress} with score {Score} and frequency {Frequency}.",
                        request.IpAddress,
                        score,
                        frequency);
                }

                _eventStore.Add(new DefenseDecision(
                    request.IpAddress,
                    action,
                    score,
                    frequency,
                    request.Path,
                    request.Signals,
                    summary,
                    request.ObservedAtUtc,
                    DateTimeOffset.UtcNow));
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Queued analysis failed for suspicious request from {IpAddress}.",
                    request.IpAddress);
            }
        }
    }

    private static int ScoreSignals(IReadOnlyList<string> signals, long frequency)
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

        score += (int)Math.Min(25, frequency * 5);
        return score;
    }
}
