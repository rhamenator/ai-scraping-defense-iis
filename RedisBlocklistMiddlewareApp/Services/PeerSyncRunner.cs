using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class PeerSyncRunner
{
    private readonly PeerSyncOptions _options;
    private readonly TimeSpan _maximumBlockLifetime;
    private readonly PeerSignalLedger _ledger;
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
        ILogger<PeerSyncRunner> logger,
        PeerSignalLedger? ledger = null)
    {
        _options = options.Value.PeerSync;
        _maximumBlockLifetime = TimeSpan.FromMinutes(Math.Max(1, options.Value.Redis.BlockDurationMinutes));
        _ledger = ledger ?? new PeerSignalLedger();
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

                    if (!IsAcceptable(peer, signal, attemptAt, out var rejection))
                    {
                        _logger.LogWarning(
                            "Rejected a peer signal from {Peer}: {Reason}.",
                            peerName,
                            rejection);
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
                        // Keep the signal's own expiry (never longer than this node's block lifetime), so a peer cannot
                        // extend a block and an old signal does not restart the clock.
                        var blockApplied = signal.ExpiresAtUtc is { } expiresAt
                            ? await _blocklistService.BlockUntilAsync(
                                normalizedIp,
                                $"peer_sync:{peerName}",
                                combinedSignals,
                                Min(expiresAt, attemptAt + _maximumBlockLifetime),
                                cancellationToken)
                            : await _blocklistService.BlockAsync(
                                normalizedIp,
                                $"peer_sync:{peerName}",
                                combinedSignals,
                                cancellationToken);
                        if (blockApplied)
                        {
                            blockedCount++;
                            blockedForPeer++;
                        }
                        else
                        {
                            observedCount++;
                            observedForPeer++;
                        }
                        _eventStore.Add(CreateImportedDecision(
                            normalizedIp,
                            blockApplied ? "blocked" : "observed",
                            blockApplied ? 90 : 0,
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

    private bool IsAcceptable(
        PeerSyncPeerOptions peer,
        PeerDefenseSignal signal,
        DateTimeOffset now,
        out string? rejection)
    {
        rejection = null;
        var peerIdentity = peer.Name.Trim();
        var hasKey = !string.IsNullOrEmpty(peer.VerificationKey);
        var hasSignature = !string.IsNullOrEmpty(signal.Signature);

        if (_options.RequireSignedSignals)
        {
            if (!hasKey)
            {
                rejection = "signatures are required but this peer has no VerificationKey";
            }
            else if (string.IsNullOrEmpty(peerIdentity) ||
                     string.IsNullOrEmpty(signal.SignalId) ||
                     string.IsNullOrEmpty(signal.Origin) ||
                     signal.ExpiresAtUtc is null ||
                     !hasSignature)
            {
                rejection = "the signal is unsigned or lacks an id, origin or expiry";
            }
        }

        // A signature that is present is always checked when a key is configured, even if signatures are optional.
        if (rejection is null && hasKey && hasSignature && !PeerSignalSigner.Verify(peer.VerificationKey, signal))
        {
            rejection = "the signature does not match";
        }

        if (rejection is null && _options.RequireSignedSignals &&
            !string.Equals(signal.Origin, peerIdentity, StringComparison.Ordinal))
        {
            rejection = "the signal's origin is not the configured peer";
        }

        if (rejection is null && signal.Hops > Math.Max(0, _options.MaximumHops))
        {
            rejection = "the signal has travelled more hops than allowed";
        }

        if (rejection is null && signal.Hops < 0)
        {
            rejection = "the signal has an invalid hop count";
        }

        if (rejection is null &&
            !string.IsNullOrEmpty(_options.NodeId) &&
            string.Equals(signal.Origin, _options.NodeId.Trim(), StringComparison.Ordinal))
        {
            rejection = "the signal originated from this node (loop)";
        }

        if (rejection is null && signal.ExpiresAtUtc is { } expiresAtUtc && expiresAtUtc <= now)
        {
            rejection = "the signal has already expired";
        }

        if (rejection is null && !string.IsNullOrEmpty(signal.SignalId) &&
            !_ledger.TryAdd(signal.SignalId, signal.ExpiresAtUtc ?? now + _maximumBlockLifetime))
        {
            rejection = "the signal was already imported";
        }

        return rejection is null;
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right) => left <= right ? left : right;

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
