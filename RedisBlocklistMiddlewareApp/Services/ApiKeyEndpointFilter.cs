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
        if (!_authenticationService.IsAuthenticated(context.HttpContext))
        {
            return ValueTask.FromResult<object?>(Results.Unauthorized());
        }

        return next(context);
    }
}
