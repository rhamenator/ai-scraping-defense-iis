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

        if (IsEmptyEquivalentRoute(options.Tarpit.PathPrefix))
        {
            errors.Add(
                "DefenseEngine:Tarpit:PathPrefix must not resolve to the root path '/'.");
        }

        if (options.Observability.EnablePrometheusEndpoint &&
            IsEmptyEquivalentRoute(options.Observability.PrometheusEndpointPath))
        {
            errors.Add(
                "DefenseEngine:Observability:PrometheusEndpointPath must not resolve to the root path '/'.");
        }

        if (!string.Equals(options.Escalation.Routing.PreferredPrimaryRoute, ThreatModelRoutes.Auto, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(options.Escalation.Routing.PreferredPrimaryRoute, ThreatModelRoutes.Local, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(options.Escalation.Routing.PreferredPrimaryRoute, ThreatModelRoutes.Remote, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"DefenseEngine:Escalation:Routing:PreferredPrimaryRoute must be '{ThreatModelRoutes.Auto}', '{ThreatModelRoutes.Local}', or '{ThreatModelRoutes.Remote}'.");
        }

        if (options.Escalation.Containment.ChallengeScoreThreshold > options.Escalation.Containment.TarpitScoreThreshold ||
            options.Escalation.Containment.TarpitScoreThreshold > options.Escalation.Containment.ThrottleScoreThreshold ||
            options.Escalation.Containment.ThrottleScoreThreshold > options.Escalation.Containment.BlockScoreThreshold)
        {
            errors.Add(
                "DefenseEngine:Escalation:Containment thresholds must increase in this order: Challenge <= Tarpit <= Throttle <= Block.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    private static bool IsEmptyEquivalentRoute(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        return path.Trim().Trim('/').Length == 0;
    }
}
