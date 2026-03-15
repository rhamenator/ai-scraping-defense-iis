using System.Net;
using System.Net.Sockets;

namespace RedisBlocklistMiddlewareApp.Services;

internal static class CidrMatcher
{
    public static bool Contains(string cidr, string ipAddress)
    {
        if (!TryParseCidr(cidr, out var networkAddress, out var prefixLength))
        {
            return false;
        }

        if (!IPAddress.TryParse(ipAddress, out var candidate))
        {
            return false;
        }

        if (candidate.AddressFamily != networkAddress.AddressFamily)
        {
            return false;
        }

        var networkBytes = networkAddress.GetAddressBytes();
        var candidateBytes = candidate.GetAddressBytes();
        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        for (var index = 0; index < fullBytes; index++)
        {
            if (networkBytes[index] != candidateBytes[index])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        var mask = (byte)(0xFF << (8 - remainingBits));
        return (networkBytes[fullBytes] & mask) == (candidateBytes[fullBytes] & mask);
    }

    private static bool TryParseCidr(
        string cidr,
        out IPAddress networkAddress,
        out int prefixLength)
    {
        networkAddress = IPAddress.None;
        prefixLength = 0;

        var parts = cidr.Split('/', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || !int.TryParse(parts[1], out prefixLength))
        {
            return false;
        }

        if (!IPAddress.TryParse(parts[0], out var parsedAddress))
        {
            return false;
        }

        networkAddress = parsedAddress;

        var maxPrefixLength = networkAddress.AddressFamily switch
        {
            AddressFamily.InterNetwork => 32,
            AddressFamily.InterNetworkV6 => 128,
            _ => 0
        };

        return prefixLength >= 0 && prefixLength <= maxPrefixLength;
    }
}
