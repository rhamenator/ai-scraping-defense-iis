using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace RedisBlocklistMiddlewareApp.Configuration;

public sealed class ProductionConfigurationValidator
{
    public IReadOnlyList<string> Validate(IHostEnvironment environment, DefenseEngineOptions options)
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

        return errors;
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
