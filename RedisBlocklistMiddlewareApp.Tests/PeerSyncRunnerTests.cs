using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class PeerSyncRunnerTests
{
    [Fact]
    public async Task RunOnceAsync_BlocksSignalsFromBlockListPeer()
    {
        var options = CreateOptions(PeerTrustModes.BlockList);
        var blocklist = new TestBlocklistService();
        var eventStore = new TestDefenseEventStore();
        var runner = new PeerSyncRunner(
            options,
            new TestPeerSignalFeedClient(new PeerDefenseSignalEnvelope(
                "peer-a",
                [
                    new PeerDefenseSignal(
                        "198.51.100.10",
                        "Peer detected scraping.",
                        ["peer_signal"],
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow)
                ])),
            blocklist,
            eventStore,
            new PeerSyncStatusStore(options),
            TestTelemetryFactory.Create(),
            NullLogger<PeerSyncRunner>.Instance);

        var status = await runner.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, status.ImportedCount);
        Assert.Equal(1, status.BlockedCount);
        Assert.Equal(0, status.ObservedCount);
        Assert.Single(blocklist.BlockCalls);
        Assert.Single(eventStore.Decisions);
        Assert.Equal("blocked", eventStore.Decisions[0].Action);
    }

    [Fact]
    public async Task RunOnceAsync_ObservesSignalsFromObserveOnlyPeer()
    {
        var options = CreateOptions(PeerTrustModes.ObserveOnly);
        var blocklist = new TestBlocklistService();
        var eventStore = new TestDefenseEventStore();
        var runner = new PeerSyncRunner(
            options,
            new TestPeerSignalFeedClient(new PeerDefenseSignalEnvelope(
                "peer-b",
                [
                    new PeerDefenseSignal(
                        "198.51.100.11",
                        "Peer detected unusual crawler behavior.",
                        ["peer_signal"],
                        DateTimeOffset.UtcNow,
                        DateTimeOffset.UtcNow)
                ])),
            blocklist,
            eventStore,
            new PeerSyncStatusStore(options),
            TestTelemetryFactory.Create(),
            NullLogger<PeerSyncRunner>.Instance);

        var status = await runner.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, status.ImportedCount);
        Assert.Equal(0, status.BlockedCount);
        Assert.Equal(1, status.ObservedCount);
        Assert.Empty(blocklist.BlockCalls);
        Assert.Single(eventStore.Decisions);
        Assert.Equal("observed", eventStore.Decisions[0].Action);
    }

    [Fact]
    public async Task RunOnceAsync_RejectsInvalidAndPrivatePeerSignals()
    {
        var options = CreateOptions(PeerTrustModes.BlockList);
        var blocklist = new TestBlocklistService();
        var eventStore = new TestDefenseEventStore();
        var runner = new PeerSyncRunner(
            options,
            new TestPeerSignalFeedClient(new PeerDefenseSignalEnvelope(
                "peer-c",
                [
                    new PeerDefenseSignal("127.0.0.1", "loopback", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                    new PeerDefenseSignal("not-an-ip", "invalid", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                    new PeerDefenseSignal("198.51.100.12", "valid", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
                ])),
            blocklist,
            eventStore,
            new PeerSyncStatusStore(options),
            TestTelemetryFactory.Create(),
            NullLogger<PeerSyncRunner>.Instance);

        var status = await runner.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, status.ImportedCount);
        Assert.Equal(2, status.RejectedCount);
        Assert.Single(blocklist.BlockCalls);
    }

    [Fact]
    public async Task RunOnceAsync_DeduplicatesEquivalentNormalizedAddresses()
    {
        var options = CreateOptions(PeerTrustModes.BlockList);
        var blocklist = new TestBlocklistService();
        var eventStore = new TestDefenseEventStore();
        var runner = new PeerSyncRunner(
            options,
            new TestPeerSignalFeedClient(new PeerDefenseSignalEnvelope(
                "peer-d",
                [
                    new PeerDefenseSignal("198.51.100.20", "ipv4", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                    new PeerDefenseSignal("::ffff:198.51.100.20", "mapped", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
                ])),
            blocklist,
            eventStore,
            new PeerSyncStatusStore(options),
            TestTelemetryFactory.Create(),
            NullLogger<PeerSyncRunner>.Instance);

        var status = await runner.RunOnceAsync(CancellationToken.None);

        Assert.Equal(1, status.ImportedCount);
        Assert.Equal(0, status.RejectedCount);
        Assert.Single(blocklist.BlockCalls);
        Assert.Equal("198.51.100.20", blocklist.BlockCalls[0].IpAddress);
    }

    [Fact]
    public async Task RunOnceAsync_CountsDroppedSignalsAfterDeduplication()
    {
        var options = Options.Create(new DefenseEngineOptions
        {
            PeerSync = new PeerSyncOptions
            {
                Enabled = true,
                MaximumSignalsPerPeer = 2,
                Peers =
                [
                    new PeerSyncPeerOptions
                    {
                        Name = "peer-e",
                        Url = "https://peer.example.test/peer-sync/signals",
                        TrustMode = PeerTrustModes.BlockList
                    }
                ]
            }
        });
        var blocklist = new TestBlocklistService();
        var runner = new PeerSyncRunner(
            options,
            new TestPeerSignalFeedClient(new PeerDefenseSignalEnvelope(
                "peer-e",
                [
                    new PeerDefenseSignal("198.51.100.21", "one", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                    new PeerDefenseSignal("::ffff:198.51.100.21", "one-dup", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                    new PeerDefenseSignal("198.51.100.22", "two", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                    new PeerDefenseSignal("198.51.100.23", "three", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
                ])),
            blocklist,
            new TestDefenseEventStore(),
            new PeerSyncStatusStore(options),
            TestTelemetryFactory.Create(),
            NullLogger<PeerSyncRunner>.Instance);

        var status = await runner.RunOnceAsync(CancellationToken.None);

        Assert.Equal(2, status.ImportedCount);
        Assert.Equal(1, status.RejectedCount);
        Assert.Equal(2, blocklist.BlockCalls.Count);
    }

    private static IOptions<DefenseEngineOptions> CreateOptions(string trustMode)
    {
        return Options.Create(new DefenseEngineOptions
        {
            PeerSync = new PeerSyncOptions
            {
                Enabled = true,
                MaximumSignalsPerPeer = 20,
                Peers =
                [
                    new PeerSyncPeerOptions
                    {
                        Name = "peer-a",
                        Url = "https://peer.example.test/peer-sync/signals",
                        TrustMode = trustMode
                    }
                ]
            }
        });
    }

    private sealed class TestPeerSignalFeedClient : IPeerSignalFeedClient
    {
        private readonly PeerDefenseSignalEnvelope _envelope;

        public TestPeerSignalFeedClient(PeerDefenseSignalEnvelope envelope)
        {
            _envelope = envelope;
        }

        public Task<PeerDefenseSignalEnvelope> FetchAsync(PeerSyncPeerOptions peer, CancellationToken cancellationToken)
        {
            return Task.FromResult(_envelope);
        }
    }

    private sealed class TestBlocklistService : IBlocklistService
    {
        public List<(string IpAddress, string Reason, IReadOnlyCollection<string> Signals)> BlockCalls { get; } = [];

        public Task<bool> IsBlockedAsync(string ipAddress, CancellationToken cancellationToken)
        {
            return Task.FromResult(false);
        }

        public Task BlockAsync(string ipAddress, string reason, IReadOnlyCollection<string> signals, CancellationToken cancellationToken)
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
}
