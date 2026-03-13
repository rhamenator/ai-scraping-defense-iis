using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class WebhookIntakeProcessingService : BackgroundService
{
    private readonly IWebhookEventInbox _inbox;
    private readonly IBlocklistService _blocklistService;
    private readonly IDefenseEventStore _eventStore;
    private readonly ILogger<WebhookIntakeProcessingService> _logger;

    public WebhookIntakeProcessingService(
        IWebhookEventInbox inbox,
        IBlocklistService blocklistService,
        IDefenseEventStore eventStore,
        ILogger<WebhookIntakeProcessingService> logger)
    {
        _inbox = inbox;
        _blocklistService = blocklistService;
        _eventStore = eventStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var item in _inbox.ReadAllAsync(stoppingToken))
        {
            try
            {
                var normalizedIp = item.Event.Details.IpAddress;
                var reason = string.IsNullOrWhiteSpace(item.Event.Reason)
                    ? "external_webhook"
                    : item.Event.Reason.Trim();
                var signals = item.Event.Details.Signals?.Count > 0
                    ? item.Event.Details.Signals
                    : [$"webhook_event:{item.Event.EventType}"];

                await _blocklistService.BlockAsync(
                    normalizedIp,
                    reason,
                    signals,
                    stoppingToken);

                _eventStore.Add(new DefenseDecision(
                    normalizedIp,
                    "blocked",
                    100,
                    1,
                    item.Event.Details.Path ?? "/",
                    signals,
                    $"Blocked from webhook intake: {reason}",
                    item.Event.TimestampUtc,
                    DateTimeOffset.UtcNow,
                    new DefenseScoreBreakdown(
                        100,
                        0,
                        100,
                        true,
                        [
                            new DefenseScoreContribution(
                                "webhook_intake",
                                100,
                                signals,
                                $"Webhook intake supplied a blocking verdict: {reason}")
                        ])));

                await _inbox.CompleteAsync(item.Id, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Webhook intake processing failed for item {ItemId}.", item.Id);
                await _inbox.AbandonAsync(item.Id, stoppingToken);
            }
        }
    }
}
