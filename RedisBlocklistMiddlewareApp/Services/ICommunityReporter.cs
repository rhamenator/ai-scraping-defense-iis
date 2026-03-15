using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public interface ICommunityReporter
{
    Task<IntakeDeliveryRecord?> ReportAsync(
        IntakeWebhookEvent webhookEvent,
        CancellationToken cancellationToken);
}

