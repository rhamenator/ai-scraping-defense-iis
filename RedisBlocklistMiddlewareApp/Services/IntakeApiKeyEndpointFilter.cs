using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class IntakeApiKeyEndpointFilter : IEndpointFilter
{
    private readonly string _headerName;
    private readonly byte[] _expectedApiKeyBytes;

    public IntakeApiKeyEndpointFilter(IOptions<DefenseEngineOptions> options)
    {
        _headerName = options.Value.Intake.ApiKeyHeaderName;
        _expectedApiKeyBytes = Encoding.UTF8.GetBytes(options.Value.Intake.ApiKey);
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
        if (!FixedTimeEquals(_expectedApiKeyBytes, suppliedApiKeyBytes))
        {
            return ValueTask.FromResult<object?>(Results.Unauthorized());
        }

        return next(context);
    }

    private static bool FixedTimeEquals(byte[] expected, byte[] supplied)
    {
        if (expected.Length != supplied.Length)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }
}
