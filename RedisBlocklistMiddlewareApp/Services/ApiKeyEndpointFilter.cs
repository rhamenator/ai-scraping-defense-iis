using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

/// <summary>
/// Endpoint filter that enforces inbound API key authentication for control-plane endpoints.
/// Checks for the X-Control-API-Key header when <see cref="DefenseEngineOptions.ControlApiKey"/> is configured.
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
            // Reject if missing, multiple values, or value doesn't match (case-sensitive secret comparison)
            if (headerValues.Count != 1
                || !string.Equals(headerValues[0], _options.ControlApiKey, StringComparison.Ordinal))
            {
                return Results.Unauthorized();
            }
        }

        return await next(context);
    }
}
