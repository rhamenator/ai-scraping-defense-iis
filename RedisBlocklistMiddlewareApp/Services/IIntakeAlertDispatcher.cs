using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public interface IIntakeAlertDispatcher
{
    Task<IReadOnlyList<IntakeDeliveryRecord>> DispatchAsync(
        IntakeWebhookEvent webhookEvent,
        CancellationToken cancellationToken);
}

