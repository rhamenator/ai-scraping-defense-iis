using System.Net;
using Microsoft.AspNetCore.Http;
using RedisBlocklistMiddlewareApp.Services;

namespace AiScrapingDefense.IntegrationTests;

internal sealed class HeaderDrivenClientIpResolver : IClientIpResolver
{
    public const string HeaderName = "X-Integration-Client-IP";

    public string? Resolve(HttpContext context)
    {
        if (context.Request.Headers.TryGetValue(HeaderName, out var suppliedIp) &&
            IPAddress.TryParse(suppliedIp.ToString(), out var parsedFromHeader))
        {
            return parsedFromHeader.IsIPv4MappedToIPv6
                ? parsedFromHeader.MapToIPv4().ToString()
                : parsedFromHeader.ToString();
        }

        var remoteIp = context.Connection.RemoteIpAddress;
        if (remoteIp is null)
        {
            return "198.51.100.150";
        }

        return remoteIp.IsIPv4MappedToIPv6
            ? remoteIp.MapToIPv4().ToString()
            : remoteIp.ToString();
    }
}
