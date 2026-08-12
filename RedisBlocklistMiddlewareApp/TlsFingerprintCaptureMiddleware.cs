using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp;

public sealed partial class TlsFingerprintCaptureMiddleware
{
    private const string ContextKey = "AiScrapingDefense.TlsClientFingerprint";
    private readonly RequestDelegate _next;
    private readonly DefenseEngineOptions _options;

    public TlsFingerprintCaptureMiddleware(
        RequestDelegate next,
        IOptions<DefenseEngineOptions> options)
    {
        _next = next;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (string.Equals(
                _options.Networking.ClientIpResolutionMode,
                ClientIpResolutionModes.TrustedProxy,
                StringComparison.OrdinalIgnoreCase))
        {
            var peerAddress = context.Connection.RemoteIpAddress;
            var isTrustedCdnPeer = _options.Cloudflare.Enabled &&
                IsTrustedPeer(peerAddress, _options.Networking.TrustedCdnProxies);
            var isTrustedCollectorPeer = IsTrustedPeer(peerAddress, _options.Networking.TrustedProxies);
            var fingerprint = isTrustedCdnPeer
                ? CaptureCloudflareFingerprint(context.Request.Headers)
                : isTrustedCollectorPeer
                    ? CaptureCollectorFingerprint(context.Request.Headers)
                    : null;
            if (fingerprint is not null)
            {
                context.Items[ContextKey] = fingerprint;
            }
        }

        await _next(context);
    }

    public static TlsClientFingerprint? GetFingerprint(HttpContext context) =>
        context.Items.TryGetValue(ContextKey, out var value)
            ? value as TlsClientFingerprint
            : null;

    internal static TlsClientFingerprint? CaptureCloudflareFingerprint(IHeaderDictionary headers)
    {
        var cloudflareJa3 = NormalizeJa3(headers["CF-JA3-Hash"]);
        var cloudflareJa4 = NormalizeJa4(headers["CF-JA4"]);
        return cloudflareJa3 is not null || cloudflareJa4 is not null
            ? new TlsClientFingerprint(cloudflareJa3, cloudflareJa4, "cloudflare")
            : null;
    }

    internal static TlsClientFingerprint? CaptureCollectorFingerprint(IHeaderDictionary headers)
    {
        var envoyJa3 = NormalizeJa3(headers["X-ASD-TLS-JA3"]);
        var envoyJa4 = NormalizeJa4(headers["X-ASD-TLS-JA4"]);
        return envoyJa3 is not null || envoyJa4 is not null
            ? new TlsClientFingerprint(envoyJa3, envoyJa4, "envoy")
            : null;
    }

    internal static bool IsTrustedPeer(IPAddress? peerAddress, IEnumerable<string> trustedEntries)
    {
        if (peerAddress is null)
        {
            return false;
        }

        var normalizedPeer = peerAddress.IsIPv4MappedToIPv6
            ? peerAddress.MapToIPv4()
            : peerAddress;
        foreach (var entry in trustedEntries)
        {
            if (IPAddress.TryParse(entry, out var address))
            {
                var normalizedAddress = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
                if (normalizedAddress.Equals(normalizedPeer))
                {
                    return true;
                }
                continue;
            }

            var parts = entry.Split('/', StringSplitOptions.TrimEntries);
            if (parts.Length == 2 &&
                IPAddress.TryParse(parts[0], out var networkAddress) &&
                int.TryParse(parts[1], out var prefixLength) &&
                new Microsoft.AspNetCore.HttpOverrides.IPNetwork(networkAddress, prefixLength)
                    .Contains(normalizedPeer))
            {
                return true;
            }
        }
        return false;
    }

    private static string? NormalizeJa3(string? value)
    {
        var candidate = value?.Trim().ToLowerInvariant();
        return candidate is not null && Ja3Pattern().IsMatch(candidate) ? candidate : null;
    }

    private static string? NormalizeJa4(string? value)
    {
        var candidate = value?.Trim().ToLowerInvariant();
        return candidate is not null && Ja4Pattern().IsMatch(candidate) ? candidate : null;
    }

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex Ja3Pattern();

    [GeneratedRegex("^[a-z0-9]{10}_[0-9a-f]{12}_[0-9a-f]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex Ja4Pattern();
}
