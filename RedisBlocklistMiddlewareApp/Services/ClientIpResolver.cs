using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class ClientIpResolver : IClientIpResolver
{
    private readonly NetworkingOptions _options;

    public ClientIpResolver(IOptions<DefenseEngineOptions> options)
    {
        _options = options.Value.Networking;
    }

    public string? Resolve(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (!IPAddress.TryParse(address.ToString(), out var parsedAddress))
        {
            return null;
        }

        var normalizedAddress = parsedAddress.ToString();
        if (string.Equals(
                _options.ClientIpResolutionMode,
                ClientIpResolutionModes.TrustedProxy,
                StringComparison.OrdinalIgnoreCase) &&
            _options.TrustedProxies
                .Concat(_options.TrustedCdnProxies)
                .Any(entry =>
                    string.Equals(entry, normalizedAddress, StringComparison.OrdinalIgnoreCase) ||
                    (entry.Contains('/') && CidrMatcher.Contains(entry, normalizedAddress))))
        {
            return null;
        }

        return normalizedAddress;
    }
}
