using System.Net;
using Microsoft.Extensions.Options;

namespace RedisBlocklistMiddlewareApp.Configuration;

public sealed class DefenseEngineOptionsValidator : IValidateOptions<DefenseEngineOptions>
{
    public ValidateOptionsResult Validate(string? name, DefenseEngineOptions options)
    {
        var errors = new List<string>();
        var networking = options.Networking;

        if (!string.Equals(networking.ClientIpResolutionMode, ClientIpResolutionModes.Direct, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(networking.ClientIpResolutionMode, ClientIpResolutionModes.TrustedProxy, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"DefenseEngine:Networking:ClientIpResolutionMode must be '{ClientIpResolutionModes.Direct}' or '{ClientIpResolutionModes.TrustedProxy}'.");
        }

        var invalidTrustedProxies = networking.TrustedProxies
            .Where(proxy => !IPAddress.TryParse(proxy, out _))
            .ToArray();

        if (invalidTrustedProxies.Length > 0)
        {
            errors.Add(
                $"DefenseEngine:Networking:TrustedProxies contains invalid IP addresses: {string.Join(", ", invalidTrustedProxies)}.");
        }

        if (string.Equals(networking.ClientIpResolutionMode, ClientIpResolutionModes.TrustedProxy, StringComparison.OrdinalIgnoreCase) &&
            networking.TrustedProxies.Length == 0)
        {
            errors.Add(
                "DefenseEngine:Networking:TrustedProxies must contain at least one IP address when ClientIpResolutionMode is 'TrustedProxy'.");
        }

        if (string.Equals(networking.ClientIpResolutionMode, ClientIpResolutionModes.Direct, StringComparison.OrdinalIgnoreCase) &&
            networking.TrustedProxies.Length > 0)
        {
            errors.Add(
                "DefenseEngine:Networking:TrustedProxies must be empty when ClientIpResolutionMode is 'Direct'.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }
}
