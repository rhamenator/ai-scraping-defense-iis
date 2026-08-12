using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class ClientIpResolverTests
{
    [Fact]
    public void Resolve_ReturnsNull_WhenRemoteIpIsMissing()
    {
        var resolver = CreateResolver();
        var context = new DefaultHttpContext();

        var result = resolver.Resolve(context);

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_NormalizesIpv4MappedIpv6Addresses()
    {
        var resolver = CreateResolver();
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("::ffff:203.0.113.10");

        var result = resolver.Resolve(context);

        Assert.Equal("203.0.113.10", result);
    }

    [Fact]
    public void Resolve_ReturnsNull_WhenForwardingLeavesTrustedCdnInfrastructureAsIdentity()
    {
        var resolver = CreateResolver(new NetworkingOptions
        {
            ClientIpResolutionMode = ClientIpResolutionModes.TrustedProxy,
            TrustedCdnProxies = ["173.245.48.0/20"]
        });
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("173.245.48.10");

        Assert.Null(resolver.Resolve(context));
    }

    private static ClientIpResolver CreateResolver(NetworkingOptions? networking = null) =>
        new(Options.Create(new DefenseEngineOptions
        {
            Networking = networking ?? new NetworkingOptions()
        }));
}
