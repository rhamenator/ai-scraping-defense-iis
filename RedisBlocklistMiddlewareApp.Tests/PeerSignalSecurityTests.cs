using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

/// <summary>Provenance, signing, hop and replay controls on peer signal exchange (#198).</summary>
public sealed class PeerSignalSecurityTests
{
    private const string Key = "peer-a-shared-signing-key-for-unit-tests-only";
    private static readonly DateTimeOffset Decided = new(2026, 11, 22, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ASignedSignal_Verifies_AndAnyChangedFieldBreaksIt()
    {
        var signed = Signed("198.51.100.20");

        Assert.True(PeerSignalSigner.Verify(Key, signed));
        Assert.False(PeerSignalSigner.Verify("a-different-key", signed));
        Assert.False(PeerSignalSigner.Verify(Key, signed with { IpAddress = "198.51.100.21" }));
        Assert.False(PeerSignalSigner.Verify(Key, signed with { Origin = "peer-b" }));
        Assert.False(PeerSignalSigner.Verify(Key, signed with { ExpiresAtUtc = signed.ExpiresAtUtc + TimeSpan.FromDays(30) }));
        Assert.False(PeerSignalSigner.Verify(Key, signed with { Hops = 0, Summary = "changed" }));
        Assert.False(PeerSignalSigner.Verify(Key, signed with { Signals = ["extra"] }));
        Assert.False(PeerSignalSigner.Verify(Key, signed with { Signature = "not-hex" }));
        Assert.False(PeerSignalSigner.Verify(Key, signed with { Signature = null }));
    }

    [Fact]
    public async Task ValidSignedSignalFromTheConfiguredPeer_IsImportedWithItsOwnExpiry()
    {
        var expiry = DateTimeOffset.UtcNow.AddHours(3);
        var harness = new Harness(blockList: true);

        var status = await harness.RunAsync(Signed("198.51.100.30", expiresAt: expiry));

        Assert.Equal(1, status.BlockedCount);
        var call = Assert.Single(harness.Blocklist.UntilCalls);
        Assert.Equal(expiry, call.ExpiresAtUtc);
    }

    [Fact]
    public async Task ExpiryIsCappedAtThisNodesBlockLifetime()
    {
        var harness = new Harness(blockList: true, maximumBlockMinutes: 60);

        await harness.RunAsync(Signed("198.51.100.31", expiresAt: DateTimeOffset.UtcNow.AddDays(30)));

        var call = Assert.Single(harness.Blocklist.UntilCalls);
        Assert.True(call.ExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(61));
    }

    [Theory]
    [InlineData("unsigned")]
    [InlineData("bad-signature")]
    [InlineData("wrong-origin")]
    [InlineData("expired")]
    [InlineData("too-many-hops")]
    [InlineData("own-origin-loop")]
    public async Task RejectedSignals_AreNeverBlockedOrRecorded(string defect)
    {
        var harness = new Harness(blockList: true, nodeId: "this-node");
        var signal = defect switch
        {
            "unsigned" => Signed("198.51.100.40") with { Signature = null },
            "bad-signature" => Signed("198.51.100.40") with { Signature = new string('0', 64) },
            "wrong-origin" => Resign(Signed("198.51.100.40") with { Origin = "peer-b" }),
            "expired" => Signed("198.51.100.40", expiresAt: DateTimeOffset.UtcNow.AddMinutes(-1)),
            "too-many-hops" => Resign(Signed("198.51.100.40") with { Hops = 1 }),
            _ => Resign(Signed("198.51.100.40") with { Origin = "this-node" })
        };

        var status = await harness.RunAsync(signal);

        Assert.Equal(1, status.RejectedCount);
        Assert.Equal(0, status.ImportedCount);
        Assert.Empty(harness.Blocklist.UntilCalls);
        Assert.Empty(harness.Blocklist.BlockCalls);
        Assert.Empty(harness.EventStore.Decisions);
    }

    [Fact]
    public async Task AReplayedSignal_IsAppliedOnlyOnce()
    {
        var harness = new Harness(blockList: true);
        var signal = Signed("198.51.100.50");

        var first = await harness.RunAsync(signal);
        var second = await harness.RunAsync(signal);

        Assert.Equal(1, first.ImportedCount);
        Assert.Equal(0, second.ImportedCount);
        Assert.Equal(1, second.RejectedCount);
        Assert.Single(harness.Blocklist.UntilCalls);
    }

    [Fact]
    public async Task APeerWithoutAVerificationKey_IsRejected_WhenSignaturesAreRequired()
    {
        var harness = new Harness(blockList: true, verificationKey: string.Empty);

        var status = await harness.RunAsync(Signed("198.51.100.60"));

        Assert.Equal(1, status.RejectedCount);
        Assert.Empty(harness.Blocklist.UntilCalls);
    }

    [Fact]
    public async Task UnsignedSignals_StillWork_WhenSignaturesAreExplicitlyNotRequired()
    {
        var harness = new Harness(blockList: false, requireSigned: false, verificationKey: string.Empty);
        var plain = new PeerDefenseSignal("198.51.100.70", "plain", ["x"], Decided, Decided);

        var status = await harness.RunAsync(plain);

        Assert.Equal(1, status.ImportedCount);
        Assert.Equal(1, status.ObservedCount);
    }

    [Fact]
    public async Task ObserveOnlyRemainsTheDefault_AndNeverBlocks()
    {
        Assert.Equal(PeerTrustModes.ObserveOnly, new PeerSyncPeerOptions().TrustMode);
        var harness = new Harness(blockList: false);

        var status = await harness.RunAsync(Signed("198.51.100.80"));

        Assert.Equal(1, status.ObservedCount);
        Assert.Equal(0, status.BlockedCount);
        Assert.Empty(harness.Blocklist.UntilCalls);
    }

    [Fact]
    public void Export_ContainsOnlyLocallyOriginatedDecisions_AndSignsThem()
    {
        var store = new ExportStore(
        [
            Decision("198.51.100.90", ["local_rule"]),
            Decision("198.51.100.91", ["peer_sync:peer-b"]),
            Decision("198.51.100.92", ["community_blocklist:list"]),
            Decision("198.51.100.93", ["webhook_intake"])
        ]);
        var options = new DefenseEngineOptions
        {
            Audit = new AuditOptions { MaxRecentEvents = 10 },
            PeerSync = new PeerSyncOptions { NodeId = "peer-a", SigningKey = Key, MaximumExportSignals = 10 }
        };

        var envelope = Program.GetPeerSignalsForExport(store, options, 10);

        Assert.Equal(["198.51.100.90", "198.51.100.93"], envelope.Signals.Select(s => s.IpAddress).ToArray());
        Assert.All(envelope.Signals, signal =>
        {
            Assert.Equal("peer-a", signal.Origin);
            Assert.Equal(0, signal.Hops);
            Assert.False(string.IsNullOrEmpty(signal.SignalId));
            Assert.NotNull(signal.ExpiresAtUtc);
            Assert.True(PeerSignalSigner.Verify(Key, signal));
        });
        Assert.Equal(2, envelope.Signals.Select(s => s.SignalId).Distinct().Count());
    }

    [Fact]
    public void Export_WithoutAKeyOrNodeId_IsUnsigned()
    {
        var store = new ExportStore([Decision("198.51.100.94", ["local_rule"])]);
        var options = new DefenseEngineOptions { PeerSync = new PeerSyncOptions { MaximumExportSignals = 10 } };

        var signal = Assert.Single(Program.GetPeerSignalsForExport(store, options, 10).Signals);

        Assert.Null(signal.Signature);
    }

    [Fact]
    public async Task AnExportedEnvelope_RoundTripsThroughTheClientAndImports()
    {
        var store = new ExportStore([Decision("198.51.100.95", ["local_rule"])]);
        var exportOptions = new DefenseEngineOptions
        {
            PeerSync = new PeerSyncOptions { NodeId = "peer-a", SigningKey = Key, MaximumExportSignals = 10 }
        };
        var json = JsonSerializer.Serialize(
            Program.GetPeerSignalsForExport(store, exportOptions, 10),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var envelope = await HttpClientFor(json).FetchAsync(
            new PeerSyncPeerOptions { Name = "peer-a", Url = "https://peer.example.test/peer-sync/signals" },
            CancellationToken.None);

        var signal = Assert.Single(envelope.Signals);
        Assert.Equal("198.51.100.95", signal.IpAddress);
        Assert.True(PeerSignalSigner.Verify(Key, signal), "the signature must survive serialization and parsing");
        Assert.Contains("\"ip_address\"", json);
    }

    private static HttpPeerSignalFeedClient HttpClientFor(string json) =>
        new(
            new StubHttpClientFactory(json),
            Options.Create(new DefenseEngineOptions { PeerSync = new PeerSyncOptions() }),
            NullLogger<HttpPeerSignalFeedClient>.Instance);

    private static PeerDefenseSignal Signed(string ip, DateTimeOffset? expiresAt = null) => Resign(new PeerDefenseSignal(
        ip,
        "Peer detected scraping.",
        ["local_rule"],
        Decided,
        Decided,
        SignalId: PeerSignalSigner.CreateSignalId("peer-a", ip, Decided),
        Origin: "peer-a",
        ExpiresAtUtc: expiresAt ?? DateTimeOffset.UtcNow.AddHours(5),
        Hops: 0));

    private static PeerDefenseSignal Resign(PeerDefenseSignal signal) =>
        signal with { Signature = PeerSignalSigner.Sign(Key, signal) };

    private static DefenseDecision Decision(string ip, string[] signals) =>
        new(ip, "blocked", 90, 1, "/x", signals, "summary", Decided, Decided);

    private sealed class Harness
    {
        public Harness(
            bool blockList,
            string nodeId = "",
            bool requireSigned = true,
            string verificationKey = Key,
            int maximumBlockMinutes = 1440)
        {
            Options = Microsoft.Extensions.Options.Options.Create(new DefenseEngineOptions
            {
                Redis = new RedisOptions { BlockDurationMinutes = maximumBlockMinutes },
                PeerSync = new PeerSyncOptions
                {
                    Enabled = true,
                    NodeId = nodeId,
                    RequireSignedSignals = requireSigned,
                    Peers =
                    [
                        new PeerSyncPeerOptions
                        {
                            Name = "peer-a",
                            Url = "https://peer.example.test/peer-sync/signals",
                            VerificationKey = verificationKey,
                            TrustMode = blockList ? PeerTrustModes.BlockList : PeerTrustModes.ObserveOnly
                        }
                    ]
                }
            });
            Ledger = new PeerSignalLedger();
        }

        public IOptions<DefenseEngineOptions> Options { get; }

        public PeerSignalLedger Ledger { get; }

        public RecordingBlocklist Blocklist { get; } = new();

        public RecordingEventStore EventStore { get; } = new();

        public Task<PeerSyncStatus> RunAsync(PeerDefenseSignal signal) =>
            new PeerSyncRunner(
                Options,
                new SingleEnvelopeClient(new PeerDefenseSignalEnvelope("peer-a", [signal])),
                Blocklist,
                EventStore,
                new PeerSyncStatusStore(Options),
                TestTelemetryFactory.Create(),
                NullLogger<PeerSyncRunner>.Instance,
                Ledger).RunOnceAsync(CancellationToken.None);
    }

    private sealed class SingleEnvelopeClient(PeerDefenseSignalEnvelope envelope) : IPeerSignalFeedClient
    {
        public Task<PeerDefenseSignalEnvelope> FetchAsync(PeerSyncPeerOptions peer, CancellationToken cancellationToken) =>
            Task.FromResult(envelope);
    }

    private sealed class RecordingBlocklist : IBlocklistService
    {
        public List<(string Ip, string Reason)> BlockCalls { get; } = [];

        public List<(string Ip, DateTimeOffset ExpiresAtUtc)> UntilCalls { get; } = [];

        public Task<bool> IsBlockedAsync(string ipAddress, CancellationToken cancellationToken) => Task.FromResult(false);

        public Task<bool> BlockAsync(string ipAddress, string reason, IReadOnlyCollection<string> signals, CancellationToken cancellationToken)
        {
            BlockCalls.Add((ipAddress, reason));
            return Task.FromResult(true);
        }

        public Task<bool> BlockUntilAsync(string ipAddress, string reason, IReadOnlyCollection<string> signals, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken)
        {
            UntilCalls.Add((ipAddress, expiresAtUtc));
            return Task.FromResult(true);
        }

        public Task UnblockAsync(string ipAddress, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private class RecordingEventStore : IDefenseEventStore
    {
        public List<DefenseDecision> Decisions { get; } = [];

        public void Add(DefenseDecision decision) => Decisions.Add(decision);

        public IReadOnlyList<DefenseDecision> GetRecent(int count) => Decisions.Take(count).ToArray();

        public DefenseDecision? GetById(long id) => Decisions.FirstOrDefault(decision => decision.Id == id);

        public DefenseDecisionFeedback AddFeedback(DefenseDecisionFeedback feedback) => feedback with { Id = 1 };

        public IReadOnlyList<DefenseDecisionFeedback> GetRecentFeedback(int count) => [];

        public DefenseEventMetrics GetMetrics() => throw new NotSupportedException();
    }

    private sealed class ExportStore : RecordingEventStore
    {
        public ExportStore(IReadOnlyList<DefenseDecision> decisions) => Decisions.AddRange(decisions);
    }

    private sealed class StubHttpClientFactory(string json) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(json));
    }

    private sealed class StubHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
    }
}
