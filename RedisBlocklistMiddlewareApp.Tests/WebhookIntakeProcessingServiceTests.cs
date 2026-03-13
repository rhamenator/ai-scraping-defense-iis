using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class WebhookIntakeProcessingServiceTests
{
    [Fact]
    public async Task ProcessingService_BlocklistsWebhookIp_AndRecordsDecision()
    {
        var inbox = new TestWebhookEventInbox();
        var blocklist = new TestBlocklistService();
        var eventStore = new TestDefenseEventStore();
        var service = new WebhookIntakeProcessingService(
            inbox,
            blocklist,
            eventStore,
            NullLogger<WebhookIntakeProcessingService>.Instance);

        await inbox.EnqueueAsync(new WebhookInboxItem(
            7,
            new IntakeWebhookEvent(
                "suspicious_activity_detected",
                "High Combined Score (0.95)",
                DateTimeOffset.UtcNow,
                new IntakeWebhookDetails(
                    "198.51.100.20",
                    "GET",
                    "/upstream",
                    string.Empty,
                    "test-agent",
                    ["upstream_signal"]))), CancellationToken.None);

        await service.StartAsync(CancellationToken.None);
        await inbox.WaitForCompletionAsync();
        await service.StopAsync(CancellationToken.None);

        Assert.Single(blocklist.BlockCalls);
        Assert.Equal("198.51.100.20", blocklist.BlockCalls[0].IpAddress);
        Assert.Single(eventStore.Decisions);
        Assert.Equal("blocked", eventStore.Decisions[0].Action);
        Assert.Equal("/upstream", eventStore.Decisions[0].Path);
    }

    private sealed class TestWebhookEventInbox : IWebhookEventInbox
    {
        private readonly Channel<WebhookInboxItem> _channel = Channel.CreateUnbounded<WebhookInboxItem>();
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<long> EnqueueAsync(IntakeWebhookEvent webhookEvent, CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task EnqueueAsync(WebhookInboxItem item, CancellationToken cancellationToken)
        {
            _channel.Writer.TryWrite(item);
            return Task.CompletedTask;
        }

        public async IAsyncEnumerable<WebhookInboxItem> ReadAllAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return item;
            }
        }

        public Task CompleteAsync(long id, CancellationToken cancellationToken)
        {
            _completed.TrySetResult();
            _channel.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public Task AbandonAsync(long id, CancellationToken cancellationToken)
        {
            _completed.TrySetException(new InvalidOperationException("Webhook item was abandoned unexpectedly."));
            _channel.Writer.TryComplete();
            return Task.CompletedTask;
        }

        public Task WaitForCompletionAsync()
        {
            return _completed.Task;
        }
    }

    private sealed class TestBlocklistService : IBlocklistService
    {
        public List<(string IpAddress, string Reason, IReadOnlyCollection<string> Signals)> BlockCalls { get; } = [];

        public Task<bool> IsBlockedAsync(string ipAddress, CancellationToken cancellationToken)
        {
            return Task.FromResult(false);
        }

        public Task BlockAsync(
            string ipAddress,
            string reason,
            IReadOnlyCollection<string> signals,
            CancellationToken cancellationToken)
        {
            BlockCalls.Add((ipAddress, reason, signals));
            return Task.CompletedTask;
        }

        public Task UnblockAsync(string ipAddress, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class TestDefenseEventStore : IDefenseEventStore
    {
        public List<DefenseDecision> Decisions { get; } = [];

        public void Add(DefenseDecision decision)
        {
            Decisions.Add(decision);
        }

        public IReadOnlyList<DefenseDecision> GetRecent(int count)
        {
            return Decisions.Take(count).ToArray();
        }

        public DefenseEventMetrics GetMetrics()
        {
            return new DefenseEventMetrics(
                Decisions.Count,
                Decisions.LongCount(decision => decision.Action == "blocked"),
                Decisions.LongCount(decision => decision.Action == "observed"),
                Decisions.OrderByDescending(decision => decision.DecidedAtUtc)
                    .Select(decision => (DateTimeOffset?)decision.DecidedAtUtc)
                    .FirstOrDefault());
        }
    }
}
