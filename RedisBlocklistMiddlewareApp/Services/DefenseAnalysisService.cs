using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class DefenseAnalysisService : BackgroundService
{
    private readonly ISuspiciousRequestQueue _queue;
    private readonly IThreatAssessmentService _threatAssessmentService;
    private readonly IBlocklistService _blocklistService;
    private readonly IDefenseEventStore _eventStore;
    private readonly DefenseTelemetry _telemetry;
    private readonly ILogger<DefenseAnalysisService> _logger;

    public DefenseAnalysisService(
        ISuspiciousRequestQueue queue,
        IThreatAssessmentService threatAssessmentService,
        IBlocklistService blocklistService,
        IDefenseEventStore eventStore,
        DefenseTelemetry telemetry,
        ILogger<DefenseAnalysisService> logger)
    {
        _queue = queue;
        _threatAssessmentService = threatAssessmentService;
        _blocklistService = blocklistService;
        _eventStore = eventStore;
        _telemetry = telemetry;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var request in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var activity = _telemetry.StartActivity("analysis.queued_request");
                activity?.SetTag("ip", request.IpAddress);
                activity?.SetTag("path", request.Path);
                var assessment = await _threatAssessmentService.AssessAsync(request, stoppingToken);
                var action = assessment.Action;
                activity?.SetTag("analysis.action", action);

                if (assessment.ShouldBlock)
                {
                    await _blocklistService.BlockAsync(
                        request.IpAddress,
                        assessment.BlockReason,
                        assessment.Signals,
                        stoppingToken);

                    _logger.LogWarning(
                        "Blocked IP {IpAddress} after queued analysis with score {Score} and frequency {Frequency}.",
                        request.IpAddress,
                        assessment.Score,
                        assessment.Frequency);
                }
                else
                {
                    _logger.LogInformation(
                        "Queued analysis selected action {Action} for suspicious request from {IpAddress} with score {Score} and frequency {Frequency}.",
                        action,
                        request.IpAddress,
                        assessment.Score,
                        assessment.Frequency);
                }

                _eventStore.Add(new DefenseDecision(
                    request.IpAddress,
                    action,
                    assessment.Score,
                    assessment.Frequency,
                    request.Path,
                    assessment.Signals,
                    assessment.Summary,
                    request.ObservedAtUtc,
                    DateTimeOffset.UtcNow,
                    assessment.Breakdown));
                _telemetry.RecordDecision(action, "queued_analysis");
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
}
