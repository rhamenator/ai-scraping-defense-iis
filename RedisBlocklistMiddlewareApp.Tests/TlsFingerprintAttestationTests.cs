using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Security;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class TlsFingerprintAttestationTests
{
    private const string Key = "0123456789abcdef0123456789abcdef";
    private const string Ja3 = "72a589da586844d7f0818ce684948eea";
    private const string Ja4 = "t13d1516h2_8daaf6152771_e5627efa2ab1";

    [Fact]
    public void Verify_RequiresMatchingContextAndFreshness()
    {
        var fingerprint = new TlsClientFingerprint(Ja3, Ja4, "envoy", Verified: true);
        var token = TlsFingerprintAttestation.Create(
            fingerprint,
            "198.51.100.7",
            "GET",
            "/products",
            Key,
            1_700_000_000);
        var attested = fingerprint with { Attestation = token };

        Assert.Equal(
            "v1:1700000000:192976122c9fbaa4cb8c2554be66f2439e020a7d470ac838f2a622b0c5829a49",
            token);

        Assert.True(TlsFingerprintAttestation.Verify(
            attested,
            "198.51.100.7",
            "GET",
            "/products",
            Key,
            60,
            1_700_000_030));
        Assert.False(TlsFingerprintAttestation.Verify(
            attested,
            "198.51.100.7",
            "GET",
            "/admin",
            Key,
            60,
            1_700_000_030));
        Assert.False(TlsFingerprintAttestation.Verify(
            attested,
            "198.51.100.7",
            "GET",
            "/products",
            Key,
            60,
            1_700_000_061));
    }

    [Fact]
    public void Verify_RejectsGetRootReplayOnPostAdmin()
    {
        var fingerprint = new TlsClientFingerprint(Ja3, Ja4, "envoy", Verified: true);
        var token = TlsFingerprintAttestation.Create(
            fingerprint,
            "198.51.100.7",
            "GET",
            "/",
            Key,
            1_700_000_000);

        Assert.False(TlsFingerprintAttestation.Verify(
            fingerprint with { Attestation = token },
            "198.51.100.7",
            "POST",
            "/admin",
            Key,
            60,
            1_700_000_030));
    }

    [Fact]
    public void Verify_AcceptsPreviousKeyDuringRotation()
    {
        const string previousKey = "abcdef0123456789abcdef0123456789";
        var fingerprint = new TlsClientFingerprint(Ja3, Ja4, "envoy", Verified: true);
        var token = TlsFingerprintAttestation.Create(
            fingerprint,
            "198.51.100.7",
            "GET",
            "/products",
            previousKey,
            1_700_000_000);

        Assert.True(TlsFingerprintAttestation.Verify(
            fingerprint with { Attestation = token },
            "198.51.100.7",
            "GET",
            "/products",
            Key,
            60,
            1_700_000_030,
            previousKey));
    }
}
