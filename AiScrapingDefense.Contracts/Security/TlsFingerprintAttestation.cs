using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Security;

public static partial class TlsFingerprintAttestation
{
    public const string Version = "v1";

    public static string? Create(
        TlsClientFingerprint? fingerprint,
        string clientIp,
        string method,
        string path,
        string key,
        long? issuedAt = null)
    {
        var normalized = Normalize(fingerprint);
        if (normalized is null || key.Length < 32)
        {
            return null;
        }

        var timestamp = issuedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var canonical = CanonicalMessage(normalized, timestamp, clientIp, method, path);
        if (canonical is null)
        {
            return null;
        }

        var signature = HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(key),
            Encoding.UTF8.GetBytes(canonical));
        return $"{Version}:{timestamp}:{Convert.ToHexString(signature).ToLowerInvariant()}";
    }

    public static bool Verify(
        TlsClientFingerprint? fingerprint,
        string clientIp,
        string method,
        string path,
        string key,
        int maxAgeSeconds,
        long? now = null,
        string? previousKey = null)
    {
        if (fingerprint?.Attestation is null ||
            (key.Length < 32 && (previousKey?.Length ?? 0) < 32) ||
            maxAgeSeconds <= 0)
        {
            return false;
        }

        var parts = fingerprint.Attestation.Trim().ToLowerInvariant().Split(':');
        if (parts.Length != 3 || parts[0] != Version)
        {
            return false;
        }
        if (!long.TryParse(
                parts[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var issuedAt) ||
            !SignaturePattern().IsMatch(parts[2]))
        {
            return false;
        }

        var current = now ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (Math.Abs(current - issuedAt) > maxAgeSeconds)
        {
            return false;
        }

        var providedBytes = Convert.FromHexString(parts[2]);
        var verified = false;
        foreach (var candidateKey in new[] { key, previousKey })
        {
            if (candidateKey is null || candidateKey.Length < 32)
            {
                continue;
            }
            var expected = Create(
                fingerprint,
                clientIp,
                method,
                path,
                candidateKey,
                issuedAt);
            if (expected is null)
            {
                continue;
            }
            var expectedHex = expected[(expected.LastIndexOf(':') + 1)..];
            var expectedBytes = Convert.FromHexString(expectedHex);
            verified |= CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
        }
        return verified;
    }

    public static TlsClientFingerprint? Normalize(TlsClientFingerprint? fingerprint)
    {
        if (fingerprint is null)
        {
            return null;
        }
        var ja3 = fingerprint.Ja3?.Trim().ToLowerInvariant();
        var ja4 = fingerprint.Ja4?.Trim().ToLowerInvariant();
        var source = fingerprint.Source?.Trim().ToLowerInvariant();
        ja3 = ja3 is not null && Ja3Pattern().IsMatch(ja3) ? ja3 : null;
        ja4 = ja4 is not null && Ja4Pattern().IsMatch(ja4) ? ja4 : null;
        if (ja3 is null && ja4 is null ||
            source is null ||
            !SourcePattern().IsMatch(source))
        {
            return null;
        }
        return fingerprint with { Ja3 = ja3, Ja4 = ja4, Source = source };
    }

    private static string? CanonicalMessage(
        TlsClientFingerprint fingerprint,
        long issuedAt,
        string clientIp,
        string method,
        string path)
    {
        string[] fields =
        [
            Version,
            issuedAt.ToString(CultureInfo.InvariantCulture),
            clientIp.Trim().ToLowerInvariant(),
            method.Trim().ToUpperInvariant(),
            path,
            fingerprint.Ja3 ?? string.Empty,
            fingerprint.Ja4 ?? string.Empty,
            fingerprint.Source
        ];
        return fields.Any(value => value.IndexOfAny(['\n', '\r', '\0']) >= 0)
            ? null
            : string.Join('\n', fields);
    }

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex Ja3Pattern();

    [GeneratedRegex("^[a-z0-9]{10}_[0-9a-f]{12}_[0-9a-f]{12}$", RegexOptions.CultureInvariant)]
    private static partial Regex Ja4Pattern();

    [GeneratedRegex("^[a-z0-9_-]{1,32}$", RegexOptions.CultureInvariant)]
    private static partial Regex SourcePattern();

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SignaturePattern();
}
