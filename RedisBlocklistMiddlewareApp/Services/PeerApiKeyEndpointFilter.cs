using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class PeerApiKeyEndpointFilter : IEndpointFilter
{
    private readonly string _headerName;
    private readonly byte[] _expectedApiKeyBytes;

    public PeerApiKeyEndpointFilter(IOptions<DefenseEngineOptions> options)
    {
        _headerName = options.Value.PeerSync.ExportApiKeyHeaderName;
        _expectedApiKeyBytes = Encoding.UTF8.GetBytes(options.Value.PeerSync.ExportApiKey);
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
