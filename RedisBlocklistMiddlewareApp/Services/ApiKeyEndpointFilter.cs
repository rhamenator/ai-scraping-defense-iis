using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

/// <summary>
/// Endpoint filter that validates the X-Control-Plane-Key header against the configured API key.
/// Validation is skipped when no key is configured (e.g. development environments).
/// </summary>
public class ApiKeyEndpointFilter : IEndpointFilter
{
    private const string ApiKeyHeader = "X-Control-Plane-Key";
    private readonly ControlPlaneSecurityOptions _options;
    private readonly ILogger<ApiKeyEndpointFilter> _logger;

    public ApiKeyEndpointFilter(IOptions<ControlPlaneSecurityOptions> options, ILogger<ApiKeyEndpointFilter> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return await next(context);
        }

        if (!context.HttpContext.Request.Headers.TryGetValue(ApiKeyHeader, out var providedKey)
            || !string.Equals(providedKey.FirstOrDefault(), _options.ApiKey, StringComparison.Ordinal))
        {
            _logger.LogWarning("Unauthorized control-plane request from {RemoteIp}: missing or invalid {Header}.",
                context.HttpContext.Connection.RemoteIpAddress,
                ApiKeyHeader);
            return Results.Unauthorized();
        }

        return await next(context);
    }
}
