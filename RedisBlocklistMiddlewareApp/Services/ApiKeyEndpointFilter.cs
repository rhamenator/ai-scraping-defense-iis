using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

/// <summary>
/// Endpoint filter that enforces inbound API key authentication for control-plane endpoints.
/// Checks for the X-Control-API-Key header when <see cref="DefenseEngineOptions.ControlApiKey"/> is configured.
/// Uses constant-time comparison to prevent timing-based side-channel attacks.
/// </summary>
public sealed class ApiKeyEndpointFilter : IEndpointFilter
{
    private const string HeaderName = "X-Control-API-Key";

    private readonly DefenseEngineOptions _options;

    public ApiKeyEndpointFilter(IOptions<DefenseEngineOptions> options)
    {
        _options = options.Value;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!string.IsNullOrWhiteSpace(_options.ControlApiKey))
        {
            var headerValues = context.HttpContext.Request.Headers[HeaderName];
            // Reject if missing or multiple values; use constant-time comparison to prevent timing attacks.
            if (headerValues.Count != 1)
            {
                return Results.Unauthorized();
            }

            var provided = Encoding.UTF8.GetBytes(headerValues[0] ?? string.Empty);
            var expected = Encoding.UTF8.GetBytes(_options.ControlApiKey);
            if (!CryptographicOperations.FixedTimeEquals(provided, expected))
            {
                return Results.Unauthorized();
            }
        }

        return await next(context);
    }
}
