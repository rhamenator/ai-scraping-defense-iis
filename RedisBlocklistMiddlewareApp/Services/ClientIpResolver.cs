using System.Net;
using Microsoft.AspNetCore.Http;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class ClientIpResolver : IClientIpResolver
{
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

        return IPAddress.TryParse(address.ToString(), out var parsedAddress)
            ? parsedAddress.ToString()
            : null;
    }
}
