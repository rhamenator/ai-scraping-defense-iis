using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class WebhookIntakeProcessingServiceTests
{
    [Fact]
    public async Task ProcessingService_BlocklistsWebhookIp_RecordsDecision_AndPersistsDeliveries()
    {
        var inbox = new TestWebhookEventInbox();
        var blocklist = new TestBlocklistService();
        var eventStore = new TestDefenseEventStore();
        var alertDispatcher = new TestIntakeAlertDispatcher(
            [
                new IntakeDeliveryRecord(
                    IntakeDeliveryTypes.Alert,
                    IntakeDeliveryChannels.GenericWebhook,
                    "198.51.100.20",
                    "High Combined Score (0.95)",
                    "https://alerts.example.test",
                    IntakeDeliveryStatuses.Succeeded,
                    "ok",
                    DateTimeOffset.UtcNow)
            ]);
        var communityReporter = new TestCommunityReporter(
            new IntakeDeliveryRecord(
                IntakeDeliveryTypes.CommunityReport,
                "AbuseIPDB",
                "198.51.100.20",
                "High Combined Score (0.95)",
                "https://abuse.example.test",
                IntakeDeliveryStatuses.Succeeded,
                "ok",
                DateTimeOffset.UtcNow));
        var deliveryStore = new TestIntakeDeliveryStore();
        var service = new WebhookIntakeProcessingService(
            inbox,
            blocklist,
            eventStore,
            alertDispatcher,
            communityReporter,
            deliveryStore,
            TestTelemetryFactory.Create(),
            NullLogger<WebhookIntakeProcessingService>.Instance);

        var webhookEvent = new IntakeWebhookEvent(
            "suspicious_activity_detected",
            "High Combined Score (0.95)",
            DateTimeOffset.UtcNow,
            new IntakeWebhookDetails(
                "198.51.100.20",
                "GET",
                "/upstream",
                string.Empty,
                "test-agent",
                ["upstream_signal"]));

        await inbox.EnqueueAsync(new WebhookInboxItem(
            7,
            webhookEvent), CancellationToken.None);

        await service.StartAsync(CancellationToken.None);
        await inbox.WaitForCompletionAsync();
        await service.StopAsync(CancellationToken.None);

        Assert.Single(blocklist.BlockCalls);
        Assert.Equal("198.51.100.20", blocklist.BlockCalls[0].IpAddress);
        Assert.Single(eventStore.Decisions);
        Assert.Equal("blocked", eventStore.Decisions[0].Action);
        Assert.Equal("/upstream", eventStore.Decisions[0].Path);
        Assert.Single(alertDispatcher.Events);
        Assert.Same(webhookEvent, alertDispatcher.Events[0]);
        Assert.Single(communityReporter.Events);
        Assert.Same(webhookEvent, communityReporter.Events[0]);
        Assert.Equal(2, deliveryStore.Records.Count);
        Assert.Contains(deliveryStore.Records, record => record.DeliveryType == IntakeDeliveryTypes.Alert);
        Assert.Contains(deliveryStore.Records, record => record.DeliveryType == IntakeDeliveryTypes.CommunityReport);
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

        public List<DefenseDecisionFeedback> Feedback { get; } = [];

        public void Add(DefenseDecision decision)
        {
            Decisions.Add(decision);
        }

        public IReadOnlyList<DefenseDecision> GetRecent(int count)
        {
            return Decisions.Take(count).ToArray();
        }

        public DefenseDecision? GetById(long id)
        {
            return Decisions.FirstOrDefault(decision => decision.Id == id);
        }

        public DefenseDecisionFeedback AddFeedback(DefenseDecisionFeedback feedback)
        {
            var persisted = feedback with { Id = Feedback.Count + 1 };
            Feedback.Add(persisted);
            return persisted;
        }

        public IReadOnlyList<DefenseDecisionFeedback> GetRecentFeedback(int count)
        {
            return Feedback.Take(count).ToArray();
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

    private sealed class TestIntakeAlertDispatcher : IIntakeAlertDispatcher
    {
        private readonly IReadOnlyList<IntakeDeliveryRecord> _deliveries;

        public TestIntakeAlertDispatcher(IReadOnlyList<IntakeDeliveryRecord> deliveries)
        {
            _deliveries = deliveries;
        }

        public List<IntakeWebhookEvent> Events { get; } = [];

        public Task<IReadOnlyList<IntakeDeliveryRecord>> DispatchAsync(
            IntakeWebhookEvent webhookEvent,
            CancellationToken cancellationToken)
        {
            Events.Add(webhookEvent);
            return Task.FromResult(_deliveries);
        }
    }

    private sealed class TestCommunityReporter : ICommunityReporter
    {
        private readonly IntakeDeliveryRecord? _delivery;

        public TestCommunityReporter(IntakeDeliveryRecord? delivery)
        {
            _delivery = delivery;
        }

        public List<IntakeWebhookEvent> Events { get; } = [];

        public Task<IntakeDeliveryRecord?> ReportAsync(
            IntakeWebhookEvent webhookEvent,
            CancellationToken cancellationToken)
        {
            Events.Add(webhookEvent);
            return Task.FromResult(_delivery);
        }
    }

    private sealed class TestIntakeDeliveryStore : IIntakeDeliveryStore
    {
        public List<IntakeDeliveryRecord> Records { get; } = [];

        public void Add(IntakeDeliveryRecord record)
        {
            Records.Add(record);
        }

        public IReadOnlyList<IntakeDeliveryRecord> GetRecent(int count)
        {
            return Records.Take(count).ToArray();
        }

        public IntakeDeliveryMetrics GetMetrics()
        {
            return new IntakeDeliveryMetrics(
                Records.Count,
                Records.LongCount(record => record.Status == IntakeDeliveryStatuses.Succeeded),
                Records.LongCount(record => record.Status == IntakeDeliveryStatuses.Failed),
                Records.LongCount(record => record.Status == IntakeDeliveryStatuses.Skipped),
                Records.OrderByDescending(record => record.AttemptedAtUtc)
                    .Select(record => (DateTimeOffset?)record.AttemptedAtUtc)
                    .FirstOrDefault());
        }
    }
}
