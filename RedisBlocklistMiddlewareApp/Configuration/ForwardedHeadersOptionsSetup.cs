using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;

namespace RedisBlocklistMiddlewareApp.Configuration;

public sealed class ForwardedHeadersOptionsSetup : IConfigureOptions<ForwardedHeadersOptions>
{
    private readonly DefenseEngineOptions _options;

    public ForwardedHeadersOptionsSetup(IOptions<DefenseEngineOptions> options)
    {
        _options = options.Value;
    }

    public void Configure(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders = ForwardedHeaders.None;
        options.ForwardLimit = 1;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        if (!string.Equals(
                _options.Networking.ClientIpResolutionMode,
                ClientIpResolutionModes.TrustedProxy,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        foreach (var trustedProxyAddress in _options.Networking.TrustedProxies
                     .Concat(_options.Networking.TrustedCdnProxies)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (IPAddress.TryParse(trustedProxyAddress, out var trustedProxy))
            {
                options.KnownProxies.Add(trustedProxy);
                continue;
            }

            var parts = trustedProxyAddress.Split('/', StringSplitOptions.TrimEntries);
            if (parts.Length == 2 &&
                IPAddress.TryParse(parts[0], out var networkAddress) &&
                int.TryParse(parts[1], out var prefixLength))
            {
                options.KnownIPNetworks.Add(new System.Net.IPNetwork(networkAddress, prefixLength));
            }
        }

        // With one CDN or ingress in front, the single trailing hop is the client. With a chain (client, CDN,
        // ingress) a limit of 1 stops on a trusted proxy address and the client is never identified. Walking the
        // whole chain is only safe because the walk stops at the first address that is not a configured proxy, so
        // entries a client prepends cannot be reached. Without any configured proxy the header is not trusted at all.
        var configuredLimit = _options.Networking.TrustedProxyHopLimit;
        var hasTrustedProxies = options.KnownProxies.Count > 0 || options.KnownIPNetworks.Count > 0;
        options.ForwardLimit = configuredLimit > 0
            ? configuredLimit
            : hasTrustedProxies ? null : 1;
    }
}
