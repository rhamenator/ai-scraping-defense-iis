using System.Net;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class CommunityBlocklistSyncRunner
{
    private readonly CommunityBlocklistOptions _options;
    private readonly ICommunityBlocklistFeedClient _feedClient;
    private readonly IBlocklistService _blocklistService;
    private readonly ICommunityBlocklistSyncStatusStore _statusStore;
    private readonly DefenseTelemetry _telemetry;
    private readonly ILogger<CommunityBlocklistSyncRunner> _logger;

    public CommunityBlocklistSyncRunner(
        IOptions<DefenseEngineOptions> options,
        ICommunityBlocklistFeedClient feedClient,
        IBlocklistService blocklistService,
        ICommunityBlocklistSyncStatusStore statusStore,
        DefenseTelemetry telemetry,
        ILogger<CommunityBlocklistSyncRunner> logger)
    {
        _options = options.Value.CommunityBlocklist;
        _feedClient = feedClient;
        _blocklistService = blocklistService;
        _statusStore = statusStore;
        _telemetry = telemetry;
        _logger = logger;
    }

    public async Task<CommunityBlocklistSyncStatus> RunOnceAsync(CancellationToken cancellationToken)
    {
        var attemptAt = DateTimeOffset.UtcNow;

        if (!_options.Enabled)
        {
            var disabledStatus = new CommunityBlocklistSyncStatus(false, attemptAt, null, 0, 0, null, []);
            _statusStore.Update(disabledStatus);
            return disabledStatus;
        }

        var sourceStatuses = new List<CommunityBlocklistSourceSyncStatus>();
        var importedCount = 0;
        var rejectedCount = 0;
        string? lastError = null;
        DateTimeOffset? lastSuccessAtUtc = null;

        using var activity = _telemetry.StartActivity("sync.community_blocklist");

        foreach (var source in _options.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Url))
            {
                continue;
            }

            try
            {
                var importedForSource = 0;
                var rejectedForSource = 0;
                var sourceName = GetSourceName(source);
                var ips = await _feedClient.FetchAsync(source, cancellationToken);
                var maxEntries = Math.Max(1, _options.MaximumEntriesPerSource);
                var truncatedCount = Math.Max(0, ips.Count - maxEntries);

                foreach (var ip in ips.Take(maxEntries))
                {
                    if (!TryValidateImportIp(ip, out var normalizedIp))
                    {
                        rejectedCount++;
                        rejectedForSource++;
                        continue;
                    }

                    await _blocklistService.BlockAsync(
                        normalizedIp,
                        $"community_blocklist:{sourceName}",
                        [$"community_blocklist:{sourceName}"],
                        cancellationToken);

                    importedCount++;
                    importedForSource++;
                }

                if (truncatedCount > 0)
                {
                    _logger.LogWarning(
                        "Community blocklist source {SourceName} had {TruncatedCount} entries truncated by the MaximumEntriesPerSource limit ({MaxEntries}).",
                        sourceName,
                        truncatedCount,
                        maxEntries);
                }

                rejectedForSource += truncatedCount;
                rejectedCount += truncatedCount;
                lastSuccessAtUtc = DateTimeOffset.UtcNow;

                sourceStatuses.Add(new CommunityBlocklistSourceSyncStatus(
                    sourceName,
                    source.Url,
                    importedForSource,
                    rejectedForSource,
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
                _logger.LogWarning(ex, "Community blocklist sync failed for source {SourceUrl}.", source.Url);
                sourceStatuses.Add(new CommunityBlocklistSourceSyncStatus(
                    GetSourceName(source),
                    source.Url,
                    0,
                    0,
                    null,
                    "Sync failed for this source."));
            }
        }

        var status = new CommunityBlocklistSyncStatus(
            true,
            attemptAt,
            lastSuccessAtUtc,
            importedCount,
            rejectedCount,
            lastError,
            sourceStatuses.ToArray());
        _telemetry.RecordCommunitySync(importedCount, rejectedCount);
        _statusStore.Update(status);
        return status;
    }

    internal static bool TryValidateImportIp(string ipAddress, out string normalizedIpAddress)
    {
        normalizedIpAddress = string.Empty;

        if (!IPAddress.TryParse(ipAddress, out var parsed))
        {
            return false;
        }

        parsed = parsed.IsIPv4MappedToIPv6 ? parsed.MapToIPv4() : parsed;
        if (IPAddress.IsLoopback(parsed) ||
            parsed.Equals(IPAddress.Any) ||
            parsed.Equals(IPAddress.IPv6Any) ||
            parsed.IsIPv6LinkLocal ||
            parsed.IsIPv6Multicast ||
            IsPrivateAddress(parsed))
        {
            return false;
        }

        normalizedIpAddress = parsed.ToString();
        return true;
    }

    private static string GetSourceName(CommunityBlocklistSourceOptions source)
    {
        return string.IsNullOrWhiteSpace(source.Name)
            ? "feed"
            : source.Name.Trim().Replace(" ", "_", StringComparison.Ordinal);
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var bytes = address.GetAddressBytes();
            return bytes[0] switch
            {
                10 => true,
                127 => true,
                172 when bytes[1] >= 16 && bytes[1] <= 31 => true,
                192 when bytes[1] == 168 => true,
                169 when bytes[1] == 254 => true,
                _ => false
            };
        }

        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var bytes = address.GetAddressBytes();
            return (bytes[0] & 0xFE) == 0xFC || address.IsIPv6SiteLocal;
        }

        return false;
    }
}
