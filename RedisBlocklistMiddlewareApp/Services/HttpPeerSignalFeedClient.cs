using System.Text.Json;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class HttpPeerSignalFeedClient : IPeerSignalFeedClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly PeerSyncOptions _options;
    private readonly ILogger<HttpPeerSignalFeedClient> _logger;

    public HttpPeerSignalFeedClient(
        IHttpClientFactory httpClientFactory,
        IOptions<DefenseEngineOptions> options,
        ILogger<HttpPeerSignalFeedClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value.PeerSync;
        _logger = logger;
    }

    public async Task<PeerDefenseSignalEnvelope> FetchAsync(
        PeerSyncPeerOptions peer,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, peer.Url);
        if (!string.IsNullOrWhiteSpace(peer.ApiKey))
        {
            request.Headers.TryAddWithoutValidation(peer.ApiKeyHeaderName, peer.ApiKey);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.RequestTimeoutSeconds)));

        var client = _httpClientFactory.CreateClient(nameof(HttpPeerSignalFeedClient));
        using var response = await client.SendAsync(request, timeoutCts.Token);
        response.EnsureSuccessStatusCode();

        await using var responseStream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: timeoutCts.Token);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            return new PeerDefenseSignalEnvelope(
                peer.Name,
                ReadLegacyIpArray(root));
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("ips", out var ipsElement) &&
            ipsElement.ValueKind == JsonValueKind.Array)
        {
            return new PeerDefenseSignalEnvelope(
                peer.Name,
                ReadLegacyIpArray(ipsElement));
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("signals", out var signalsElement) &&
            signalsElement.ValueKind == JsonValueKind.Array)
        {
            var source = root.TryGetProperty("source", out var sourceElement) &&
                sourceElement.ValueKind == JsonValueKind.String
                ? sourceElement.GetString() ?? peer.Name
                : peer.Name;

            return new PeerDefenseSignalEnvelope(
                string.IsNullOrWhiteSpace(source) ? peer.Name : source,
                ReadSignalEnvelope(signalsElement));
        }

        _logger.LogWarning("Peer signal feed {Url} returned an unexpected payload.", peer.Url);
        return new PeerDefenseSignalEnvelope(peer.Name, []);
    }

    private static IReadOnlyList<PeerDefenseSignal> ReadLegacyIpArray(JsonElement arrayElement)
    {
        var now = DateTimeOffset.UtcNow;
        return arrayElement.EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.String)
            .Select(element => element.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => new PeerDefenseSignal(
                value!,
                "Imported from peer legacy IP list.",
                ["peer_sync:legacy_feed"],
                now,
                now))
            .ToArray();
    }

    private static IReadOnlyList<PeerDefenseSignal> ReadSignalEnvelope(JsonElement signalsElement)
    {
        var signals = new List<PeerDefenseSignal>();

        foreach (var element in signalsElement.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object ||
                !element.TryGetProperty("ip_address", out var ipElement) ||
                ipElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var summary = element.TryGetProperty("summary", out var summaryElement) &&
                summaryElement.ValueKind == JsonValueKind.String
                ? summaryElement.GetString() ?? string.Empty
                : string.Empty;
            var observedAtUtc = element.TryGetProperty("observed_at_utc", out var observedElement) &&
                observedElement.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(observedElement.GetString(), out var observedAt)
                ? observedAt
                : DateTimeOffset.UtcNow;
            var decidedAtUtc = element.TryGetProperty("decided_at_utc", out var decidedElement) &&
                decidedElement.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(decidedElement.GetString(), out var decidedAt)
                ? decidedAt
                : observedAtUtc;
            var signalList = element.TryGetProperty("signals", out var signalElement) &&
                signalElement.ValueKind == JsonValueKind.Array
                ? signalElement.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!)
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .ToArray()
                : Array.Empty<string>();

            signals.Add(new PeerDefenseSignal(
                ipElement.GetString()!,
                summary,
                signalList,
                observedAtUtc,
                decidedAtUtc));
        }

        return signals;
    }
}
