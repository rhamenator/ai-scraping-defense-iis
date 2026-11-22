using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

/// <summary>Runs the real forwarded-headers middleware with the app's own setup, then the app's resolver (#187).</summary>
public sealed class ForwardedChainTests
{
    private const string Ingress = "10.0.0.5";
    private const string Cdn = "198.51.100.7";
    private const string Client = "203.0.113.9";

    [Fact]
    public async Task ClientBehindCdnAndIngress_IsIdentified_WhenBothHopsAreTrusted()
    {
        var resolved = await ResolveAsync(peer: Ingress, forwardedFor: $"{Client}, {Cdn}", trusted: [Ingress, Cdn]);

        Assert.Equal(Client, resolved);
    }

    [Fact]
    public async Task ChainWithOnlyTheIngressTrusted_StopsAtTheFirstUntrustedAddress()
    {
        var resolved = await ResolveAsync(peer: Ingress, forwardedFor: $"{Client}, {Cdn}", trusted: [Ingress]);

        Assert.Equal(Cdn, resolved);
    }

    [Fact]
    public async Task EntriesAClientPrepends_AreNeverReached()
    {
        var resolved = await ResolveAsync(
            peer: Ingress,
            forwardedFor: $"6.6.6.6, {Client}, {Cdn}",
            trusted: [Ingress, Cdn]);

        Assert.Equal(Client, resolved);
    }

    [Fact]
    public async Task ForwardedHeaderFromAnUntrustedPeer_IsIgnored()
    {
        var resolved = await ResolveAsync(peer: "192.0.2.50", forwardedFor: "6.6.6.6", trusted: [Ingress, Cdn]);

        Assert.Equal("192.0.2.50", resolved);
    }

    [Fact]
    public async Task ConfiguredHopLimitOfOne_LeavesAProxyAddress_SoTheResolverRefusesToGuess()
    {
        // The previous fixed behaviour: one hop consumed, the CDN address is left, and the resolver returns no client.
        var resolved = await ResolveAsync(
            peer: Ingress,
            forwardedFor: $"{Client}, {Cdn}",
            trusted: [Ingress, Cdn],
            hopLimit: 1);

        Assert.Null(resolved);
    }

    [Fact]
    public void WithoutAnyTrustedProxy_TheHopLimitStaysAtOne()
    {
        var options = new ForwardedHeadersOptions();
        new ForwardedHeadersOptionsSetup(Options.Create(new DefenseEngineOptions
        {
            Networking = new NetworkingOptions { ClientIpResolutionMode = ClientIpResolutionModes.TrustedProxy }
        })).Configure(options);

        Assert.Equal(1, options.ForwardLimit);
    }

    private static async Task<string?> ResolveAsync(string peer, string forwardedFor, string[] trusted, int hopLimit = 0)
    {
        var engine = new DefenseEngineOptions
        {
            Networking = new NetworkingOptions
            {
                ClientIpResolutionMode = ClientIpResolutionModes.TrustedProxy,
                TrustedProxies = trusted,
                TrustedProxyHopLimit = hopLimit
            }
        };
        var forwarded = new ForwardedHeadersOptions();
        new ForwardedHeadersOptionsSetup(Options.Create(engine)).Configure(forwarded);

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Headers["X-Forwarded-For"] = forwardedFor;
        var middleware = new ForwardedHeadersMiddleware(
            _ => Task.CompletedTask,
            NullLoggerFactory.Instance,
            Options.Create(forwarded));
        await middleware.Invoke(context);

        return new ClientIpResolver(Options.Create(engine)).Resolve(context);
    }
}
