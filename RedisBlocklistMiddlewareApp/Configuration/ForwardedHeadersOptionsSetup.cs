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
        options.KnownNetworks.Clear();
        options.KnownProxies.Clear();

        if (!string.Equals(
                _options.Networking.ClientIpResolutionMode,
                ClientIpResolutionModes.TrustedProxy,
                StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        foreach (var trustedProxyAddress in _options.Networking.TrustedProxies)
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
                options.KnownNetworks.Add(
                    new Microsoft.AspNetCore.HttpOverrides.IPNetwork(networkAddress, prefixLength));
            }
        }
    }
}
