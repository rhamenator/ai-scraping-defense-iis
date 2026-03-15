using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class PeerSyncRunner
{
    private readonly PeerSyncOptions _options;
    private readonly IPeerSignalFeedClient _feedClient;
    private readonly IBlocklistService _blocklistService;
    private readonly IDefenseEventStore _eventStore;
    private readonly IPeerSyncStatusStore _statusStore;
    private readonly DefenseTelemetry _telemetry;
    private readonly ILogger<PeerSyncRunner> _logger;

    public PeerSyncRunner(
        IOptions<DefenseEngineOptions> options,
        IPeerSignalFeedClient feedClient,
        IBlocklistService blocklistService,
        IDefenseEventStore eventStore,
        IPeerSyncStatusStore statusStore,
        DefenseTelemetry telemetry,
        ILogger<PeerSyncRunner> logger)
    {
        _options = options.Value.PeerSync;
        _feedClient = feedClient;
        _blocklistService = blocklistService;
        _eventStore = eventStore;
        _statusStore = statusStore;
        _telemetry = telemetry;
        _logger = logger;
    }

    public async Task<PeerSyncStatus> RunOnceAsync(CancellationToken cancellationToken)
    {
        var attemptAt = DateTimeOffset.UtcNow;

        if (!_options.Enabled)
        {
            var disabledStatus = new PeerSyncStatus(false, attemptAt, null, 0, 0, 0, 0, null, []);
            _statusStore.Update(disabledStatus);
            return disabledStatus;
        }

        var peerStatuses = new List<PeerSyncPeerStatus>();
        var importedCount = 0;
        var blockedCount = 0;
        var observedCount = 0;
        var rejectedCount = 0;
        string? lastError = null;
        DateTimeOffset? lastSuccessAtUtc = null;

        using var activity = _telemetry.StartActivity("sync.peer");

        foreach (var peer in _options.Peers)
        {
            if (string.IsNullOrWhiteSpace(peer.Url))
            {
                continue;
            }

            try
            {
                var peerName = GetPeerName(peer);
                var blockedForPeer = 0;
                var observedForPeer = 0;
                var rejectedForPeer = 0;
                var importedForPeer = 0;
                var maxSignals = Math.Max(1, _options.MaximumSignalsPerPeer);
                var envelope = await _feedClient.FetchAsync(peer, cancellationToken);
                var normalizedSignals = new List<(string NormalizedIp, PeerDefenseSignal Signal)>();

                foreach (var signal in envelope.Signals)
                {
                    if (!CommunityBlocklistSyncRunner.TryValidateImportIp(signal.IpAddress, out var normalizedIp))
                    {
                        rejectedCount++;
                        rejectedForPeer++;
                        continue;
                    }

                    normalizedSignals.Add((normalizedIp, signal));
                }

                var uniqueSignals = normalizedSignals
                    .GroupBy(signal => signal.NormalizedIp, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .ToArray();
                var droppedCount = Math.Max(0, uniqueSignals.Length - maxSignals);

                foreach (var (normalizedIp, signal) in uniqueSignals.Take(maxSignals))
                {
                    importedCount++;
                    importedForPeer++;
                    var combinedSignals = signal.Signals
                        .Append($"peer_sync:{peerName}")
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    var summary = string.IsNullOrWhiteSpace(signal.Summary)
                        ? $"Peer sync imported a defense signal from {peerName}."
                        : signal.Summary;

                    if (string.Equals(peer.TrustMode, PeerTrustModes.BlockList, StringComparison.OrdinalIgnoreCase))
                    {
                        await _blocklistService.BlockAsync(
                            normalizedIp,
                            $"peer_sync:{peerName}",
                            combinedSignals,
                            cancellationToken);

                        blockedCount++;
                        blockedForPeer++;
                        _eventStore.Add(CreateImportedDecision(
                            normalizedIp,
                            "blocked",
                            90,
                            summary,
                            combinedSignals,
                            signal.ObservedAtUtc));
                    }
                    else
                    {
                        observedCount++;
                        observedForPeer++;
                        _eventStore.Add(CreateImportedDecision(
                            normalizedIp,
                            "observed",
                            35,
                            $"Peer signal observed from {peerName}. Trust mode prevented auto-blocking. {summary}",
                            combinedSignals,
                            signal.ObservedAtUtc));
                    }
                }

                rejectedForPeer += droppedCount;
                rejectedCount += droppedCount;
                lastSuccessAtUtc = DateTimeOffset.UtcNow;

                peerStatuses.Add(new PeerSyncPeerStatus(
                    peerName,
                    peer.Url,
                    peer.TrustMode,
                    importedForPeer,
                    blockedForPeer,
                    observedForPeer,
                    rejectedForPeer,
                    lastSuccessAtUtc,
                    null));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                _logger.LogWarning(ex, "Peer sync failed for peer {PeerUrl}.", peer.Url);
                peerStatuses.Add(new PeerSyncPeerStatus(
                    GetPeerName(peer),
                    peer.Url,
                    peer.TrustMode,
                    0,
                    0,
                    0,
                    0,
                    null,
                    "Sync failed for this peer."));
            }
        }

        var status = new PeerSyncStatus(
            true,
            attemptAt,
            lastSuccessAtUtc,
            importedCount,
            blockedCount,
            observedCount,
            rejectedCount,
            lastError,
            peerStatuses.ToArray());
        _telemetry.RecordPeerSync(blockedCount, observedCount, rejectedCount);
        _statusStore.Update(status);
        return status;
    }

    private static string GetPeerName(PeerSyncPeerOptions peer)
    {
        return string.IsNullOrWhiteSpace(peer.Name)
            ? "peer"
            : peer.Name.Trim().Replace(" ", "_", StringComparison.Ordinal);
    }

    private static DefenseDecision CreateImportedDecision(
        string ipAddress,
        string action,
        int score,
        string summary,
        IReadOnlyList<string> signals,
        DateTimeOffset observedAtUtc)
    {
        return new DefenseDecision(
            ipAddress,
            action,
            score,
            0,
            "/peer-sync",
            signals,
            summary,
            observedAtUtc,
            DateTimeOffset.UtcNow,
            new DefenseScoreBreakdown(
                score,
                0,
                score,
                string.Equals(action, "blocked", StringComparison.OrdinalIgnoreCase),
                [
                    new DefenseScoreContribution(
                        "peer_sync",
                        score,
                        signals,
                        summary)
                ]));
    }
}
