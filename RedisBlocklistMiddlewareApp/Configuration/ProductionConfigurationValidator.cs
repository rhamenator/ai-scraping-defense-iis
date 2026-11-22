using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace RedisBlocklistMiddlewareApp.Configuration;

public sealed class ProductionConfigurationValidator
{
    public IReadOnlyList<string> Validate(
        IHostEnvironment environment,
        DefenseEngineOptions options,
        string? listenerUrls = null,
        bool hostedByIis = false)
    {
        var errors = new List<string>();

        if (!environment.IsProduction())
        {
            return errors;
        }

        if (!options.Redis.AllowLoopbackConnectionStringInProduction &&
            UsesLoopbackRedisEndpoint(options.Redis.ConnectionString))
        {
            errors.Add(
                "DefenseEngine:Redis:ConnectionString points at a loopback Redis endpoint. Set a non-loopback production Redis host or explicitly enable DefenseEngine:Redis:AllowLoopbackConnectionStringInProduction.");
        }

        if (options.Management.RequireSecureTransport &&
            !string.IsNullOrWhiteSpace(options.Management.ApiKey) &&
            !hostedByIis &&
            !ProvidesSecureTransport(options, listenerUrls))
        {
            errors.Add(
                "DefenseEngine:Management:RequireSecureTransport is enabled (the default) but no HTTPS is configured. Add an https:// listener (ASPNETCORE_URLS), or behind a TLS-terminating proxy set DefenseEngine:Networking:ClientIpResolutionMode to TrustedProxy and list the proxy in DefenseEngine:Networking:TrustedProxies so its X-Forwarded-Proto is honoured. For an HTTP-only development or test deployment set DefenseEngine:Management:RequireSecureTransport to false.");
        }

        return errors;
    }

    /// <summary>Advice that does not stop startup: management is exposed in Production without the HTTPS requirement.</summary>
    public IReadOnlyList<string> Warnings(IHostEnvironment environment, DefenseEngineOptions options)
    {
        var warnings = new List<string>();
        if (environment.IsProduction() &&
            !string.IsNullOrWhiteSpace(options.Management.ApiKey) &&
            !options.Management.RequireSecureTransport)
        {
            warnings.Add(
                "DefenseEngine:Management:RequireSecureTransport is explicitly off while the management API is enabled in Production. Management credentials and session cookies can then travel over plain HTTP. Turn it back on once HTTPS (or a TLS-terminating proxy in TrustedProxy mode with TrustedProxies set) is in place.");
        }

        return warnings;
    }

    private static bool ProvidesSecureTransport(DefenseEngineOptions options, string? listenerUrls)
    {
        if (listenerUrls?.Contains("https://", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        // The forwarded scheme is only honoured from listed proxies; without any, X-Forwarded-Proto is ignored and every
        // management request would look like plain HTTP.
        return string.Equals(
                   options.Networking.ClientIpResolutionMode,
                   ClientIpResolutionModes.TrustedProxy,
                   StringComparison.OrdinalIgnoreCase)
               && (options.Networking.TrustedProxies.Length > 0 || options.Networking.TrustedCdnProxies.Length > 0);
    }

    private static bool UsesLoopbackRedisEndpoint(string connectionString)
    {
        foreach (var segment in connectionString.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (segment.Contains('='))
            {
                continue;
            }

            var host = ExtractHost(segment);
            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(host, "::1", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string ExtractHost(string endpoint)
    {
        if (endpoint.StartsWith('['))
        {
            var endBracket = endpoint.IndexOf(']');
            return endBracket > 1
                ? endpoint[1..endBracket]
                : endpoint;
        }

        var colonIndex = endpoint.IndexOf(':');
        return colonIndex > 0
            ? endpoint[..colonIndex]
            : endpoint;
    }
}
