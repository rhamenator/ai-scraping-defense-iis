using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class PublicBlocklistApiKeyEndpointFilter : IEndpointFilter
{
    private readonly string _headerName;
    private readonly byte[] _expectedApiKeyBytes;

    public PublicBlocklistApiKeyEndpointFilter(IOptions<DefenseEngineOptions> options)
    {
        _headerName = options.Value.PublicBlocklist.ApiKeyHeaderName;
        _expectedApiKeyBytes = Encoding.UTF8.GetBytes(options.Value.PublicBlocklist.ApiKey);
    }

    public ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        if (!context.HttpContext.Request.Headers.TryGetValue(_headerName, out var suppliedApiKey))
        {
            return ValueTask.FromResult<object?>(Results.Unauthorized());
        }

        var suppliedApiKeyBytes = Encoding.UTF8.GetBytes(suppliedApiKey.ToString());
        if (_expectedApiKeyBytes.Length != suppliedApiKeyBytes.Length ||
            !CryptographicOperations.FixedTimeEquals(_expectedApiKeyBytes, suppliedApiKeyBytes))
        {
            return ValueTask.FromResult<object?>(Results.Unauthorized());
        }

        return next(context);
    }
}
