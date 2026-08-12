using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class TlsFingerprintCaptureMiddlewareTests
{
    private const string Ja3 = "72a589da586844d7f0818ce684948eea";
    private const string Ja4 = "t13d1516h2_8daaf6152771_e5627efa2ab1";

    [Fact]
    public async Task CapturesCollectorValuesOnlyFromTrustedPeer()
    {
        var trusted = CreateContext("10.0.0.8");
        trusted.Request.Headers["X-ASD-TLS-JA3"] = Ja3.ToUpperInvariant();
        trusted.Request.Headers["X-ASD-TLS-JA4"] = Ja4.ToUpperInvariant();
        await CreateMiddleware(["10.0.0.0/24"]).InvokeAsync(trusted);

        var fingerprint = TlsFingerprintCaptureMiddleware.GetFingerprint(trusted);
        Assert.NotNull(fingerprint);
        Assert.Equal(Ja3, fingerprint.Ja3);
        Assert.Equal(Ja4, fingerprint.Ja4);
        Assert.Equal("envoy", fingerprint.Source);

        var direct = CreateContext("198.51.100.8");
        direct.Request.Headers["X-ASD-TLS-JA3"] = Ja3;
        await CreateMiddleware(["10.0.0.0/24"]).InvokeAsync(direct);
        Assert.Null(TlsFingerprintCaptureMiddleware.GetFingerprint(direct));
    }

    [Fact]
    public async Task CloudflareHeadersRequireEnabledIntegrationAndValidFormat()
    {
        var context = CreateContext("173.245.48.10");
        context.Request.Headers["CF-JA3-Hash"] = Ja3;
        context.Request.Headers["CF-JA4"] = Ja4;
        await CreateMiddleware([], ["173.245.48.0/20"], cloudflareEnabled: true).InvokeAsync(context);
        Assert.Equal("cloudflare", TlsFingerprintCaptureMiddleware.GetFingerprint(context)?.Source);

        var malformed = CreateContext("173.245.48.10");
        malformed.Request.Headers["CF-JA3-Hash"] = "malformed";
        malformed.Request.Headers["CF-JA4"] = "malformed";
        await CreateMiddleware([], ["173.245.48.0/20"], cloudflareEnabled: true).InvokeAsync(malformed);
        Assert.Null(TlsFingerprintCaptureMiddleware.GetFingerprint(malformed));
    }

    [Fact]
    public async Task CloudflarePeerNeverFallsBackToClientSuppliedCollectorHeaders()
    {
        var context = CreateContext("173.245.48.10");
        context.Request.Headers["X-ASD-TLS-JA3"] = Ja3;
        context.Request.Headers["X-ASD-TLS-JA4"] = Ja4;

        await CreateMiddleware(
            ["10.0.0.0/24"],
            ["173.245.48.0/20"],
            cloudflareEnabled: true).InvokeAsync(context);

        Assert.Null(TlsFingerprintCaptureMiddleware.GetFingerprint(context));
    }

    private static DefaultHttpContext CreateContext(string peerIp)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse(peerIp);
        return context;
    }

    private static TlsFingerprintCaptureMiddleware CreateMiddleware(
        string[] trustedProxies,
        string[]? trustedCdnProxies = null,
        bool cloudflareEnabled = false)
    {
        var options = new DefenseEngineOptions
        {
            Networking = new NetworkingOptions
            {
                ClientIpResolutionMode = ClientIpResolutionModes.TrustedProxy,
                TrustedProxies = trustedProxies,
                TrustedCdnProxies = trustedCdnProxies ?? []
            },
            Cloudflare = new CloudflareOptions { Enabled = cloudflareEnabled }
        };
        return new TlsFingerprintCaptureMiddleware(_ => Task.CompletedTask, Options.Create(options));
    }
}
