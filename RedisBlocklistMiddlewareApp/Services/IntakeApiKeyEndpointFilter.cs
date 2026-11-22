using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class IntakeApiKeyEndpointFilter : IEndpointFilter
{
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly string _headerName;
    private readonly byte[] _expectedApiKeyBytes;
    private readonly int _requestsPerMinute;
    private readonly int _failedAttemptsPerMinute;
    private readonly FixedWindowCounter _counter;

    public IntakeApiKeyEndpointFilter(IOptions<DefenseEngineOptions> options, FixedWindowCounter? counter = null)
    {
        _headerName = options.Value.Intake.ApiKeyHeaderName;
        _expectedApiKeyBytes = Encoding.UTF8.GetBytes(options.Value.Intake.ApiKey);
        _requestsPerMinute = options.Value.Intake.RateLimitPerMinute;
        _failedAttemptsPerMinute = options.Value.Intake.FailedAuthLimitPerMinute;
        _counter = counter ?? new FixedWindowCounter();
    }

    public ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var failureKey = "intake-fail:" + (http.Connection.RemoteIpAddress?.ToString() ?? "unknown");

        // A client that keeps presenting wrong credentials is refused before the key is compared, so the key cannot be
        // guessed at network speed. Everything from that address is refused until the window ends, a valid key included.
        if (_counter.IsExhausted(failureKey, _failedAttemptsPerMinute, Window, out var lockedOutFor))
        {
            return ValueTask.FromResult<object?>(TooManyRequests(http, lockedOutFor));
        }

        if (!http.Request.Headers.TryGetValue(_headerName, out var suppliedApiKey) ||
            !FixedTimeEquals(_expectedApiKeyBytes, Encoding.UTF8.GetBytes(suppliedApiKey.ToString())))
        {
            _counter.TryAcquire(failureKey, _failedAttemptsPerMinute, Window, out _);
            return ValueTask.FromResult<object?>(Results.Unauthorized());
        }

        // Per credential: the key may send at most this many requests a minute, however many addresses it comes from.
        if (!_counter.TryAcquire("intake-credential", _requestsPerMinute, Window, out var retryAfter))
        {
            return ValueTask.FromResult<object?>(TooManyRequests(http, retryAfter));
        }

        return next(context);
    }

    private static IResult TooManyRequests(HttpContext http, TimeSpan retryAfter)
    {
        var seconds = Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds));
        http.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return Results.StatusCode(StatusCodes.Status429TooManyRequests);
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
