using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

/// <summary>
/// Signs and verifies peer signals with a shared secret (HMAC-SHA256). The signature covers every field an
/// importer acts on, so a signal cannot be re-labelled with another origin, a longer expiry, a different address
/// or a lower hop count without invalidating it.
/// </summary>
public static class PeerSignalSigner
{
    private const string Version = "asd-peer-signal-v1";

    public static string Sign(string key, PeerDefenseSignal signal)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(Canonicalize(signal)));
        return Convert.ToHexString(mac).ToLowerInvariant();
    }

    public static bool Verify(string key, PeerDefenseSignal signal)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(signal.Signature))
        {
            return false;
        }

        byte[] supplied;
        try
        {
            supplied = Convert.FromHexString(signal.Signature);
        }
        catch (FormatException)
        {
            return false;
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(Canonicalize(signal)));
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }

    public static string CreateSignalId(string origin, string ipAddress, DateTimeOffset decidedAtUtc)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{origin}\n{ipAddress}\n{decidedAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture)}"));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    private static string Canonicalize(PeerDefenseSignal signal) =>
        string.Join(
            '\n',
            Version,
            signal.Origin ?? string.Empty,
            signal.SignalId ?? string.Empty,
            signal.IpAddress,
            Format(signal.ObservedAtUtc),
            Format(signal.DecidedAtUtc),
            signal.ExpiresAtUtc is { } expires ? Format(expires) : string.Empty,
            signal.Hops.ToString(CultureInfo.InvariantCulture),
            signal.Summary,
            string.Join('\u001f', signal.Signals));

    private static string Format(DateTimeOffset value) =>
        value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
