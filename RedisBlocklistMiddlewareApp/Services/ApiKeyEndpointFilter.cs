using Microsoft.AspNetCore.Http;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class ApiKeyEndpointFilter : IEndpointFilter
{
    private readonly ManagementAuthenticationService _authenticationService;

    public ApiKeyEndpointFilter(ManagementAuthenticationService authenticationService)
    {
        _authenticationService = authenticationService;
    }

    public ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        if (!_authenticationService.IsAuthenticated(httpContext))
        {
            return ValueTask.FromResult<object?>(Results.Unauthorized());
        }

        if (!_authenticationService.HasValidApiKeyHeader(httpContext) &&
            IsUnsafeMethod(httpContext.Request.Method) &&
            !IsTrustedSameOriginMutation(httpContext.Request))
        {
            return ValueTask.FromResult<object?>(Results.StatusCode(StatusCodes.Status403Forbidden));
        }

        return next(context);
    }

    private static bool IsUnsafeMethod(string method) =>
        HttpMethods.IsPost(method) ||
        HttpMethods.IsPut(method) ||
        HttpMethods.IsPatch(method) ||
        HttpMethods.IsDelete(method);

    private static bool IsTrustedSameOriginMutation(HttpRequest request)
    {
        var secFetchSite = request.Headers["Sec-Fetch-Site"].ToString().Trim();
        if (!string.IsNullOrEmpty(secFetchSite) &&
            !string.Equals(secFetchSite, "same-origin", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(secFetchSite, "none", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var origin = request.Headers.Origin.ToString().Trim();
        if (string.IsNullOrEmpty(origin))
        {
            return string.Equals(secFetchSite, "same-origin", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(secFetchSite, "none", StringComparison.OrdinalIgnoreCase);
        }

        if (!request.Host.HasValue)
        {
            return false;
        }

        var expectedOrigin = $"{request.Scheme}://{request.Host.Value}";
        return string.Equals(origin, expectedOrigin, StringComparison.OrdinalIgnoreCase);
    }
}
