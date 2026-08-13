using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Security;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class McpModelAdapterTests
{
    [Fact]
    public void BuildClassifyPayload_BindsVerifiedInfrastructureFingerprint()
    {
        const string key = "0123456789abcdef0123456789abcdef";
        var options = Options.Create(new DefenseEngineOptions
        {
            TlsFingerprints = new TlsFingerprintOptions { AttestationKey = key }
        });
        var adapter = new McpModelAdapter(options, NullLogger<McpModelAdapter>.Instance);
        var fingerprint = new TlsClientFingerprint(
            "72a589da586844d7f0818ce684948eea",
            "t13d1516h2_8daaf6152771_e5627efa2ab1",
            "envoy",
            Verified: true);
        var context = new ThreatAssessmentContext(
            "198.51.100.7",
            "GET",
            "/products",
            string.Empty,
            "Mozilla/5.0",
            [],
            1,
            0,
            0,
            fingerprint);
        var method = typeof(McpModelAdapter).GetMethod(
            "BuildClassifyPayload",
            BindingFlags.Instance | BindingFlags.NonPublic);

        var payload = JsonSerializer.SerializeToElement(method!.Invoke(adapter, [context]));
        var token = payload.GetProperty("tls_fingerprint_attestation").GetString();

        Assert.True(TlsFingerprintAttestation.Verify(
            fingerprint with { Attestation = token },
            context.IpAddress,
            context.Method,
            context.Path,
            key,
            60));
    }
}
