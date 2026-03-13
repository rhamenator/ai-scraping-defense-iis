using System.Net;
using Microsoft.AspNetCore.Http;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class ClientIpResolverTests
{
    [Fact]
    public void Resolve_ReturnsNull_WhenRemoteIpIsMissing()
    {
        var resolver = new ClientIpResolver();
        var context = new DefaultHttpContext();

        var result = resolver.Resolve(context);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_NormalizesIpv4MappedIpv6Addresses()
    {
        var resolver = new ClientIpResolver();
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:203.0.113.10");

        var result = resolver.Resolve(context);

        Assert.Equal("203.0.113.10", result);
    }
}
