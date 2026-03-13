using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public interface IWebhookEventInbox
{
    Task<long> EnqueueAsync(IntakeWebhookEvent webhookEvent, CancellationToken cancellationToken);

    IAsyncEnumerable<WebhookInboxItem> ReadAllAsync(CancellationToken cancellationToken);

    Task CompleteAsync(long id, CancellationToken cancellationToken);

    Task AbandonAsync(long id, CancellationToken cancellationToken);
}
