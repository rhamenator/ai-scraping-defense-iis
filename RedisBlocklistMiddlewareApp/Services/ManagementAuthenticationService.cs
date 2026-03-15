using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class ManagementAuthenticationService
{
    public const string SessionCookieName = "defense_dashboard_session";

    private readonly string _headerName;
    private readonly byte[] _expectedApiKeyBytes;
    private readonly IDataProtector _dataProtector;
    private readonly TimeSpan _sessionLifetime;

    public ManagementAuthenticationService(
        IOptions<DefenseEngineOptions> options,
        IDataProtectionProvider dataProtectionProvider)
    {
        _headerName = options.Value.Management.ApiKeyHeaderName;
        _expectedApiKeyBytes = Encoding.UTF8.GetBytes(options.Value.Management.ApiKey);
        _dataProtector = dataProtectionProvider.CreateProtector("management-dashboard-session");
        _sessionLifetime = TimeSpan.FromHours(options.Value.Management.DashboardSessionHours);
    }

    public bool IsAuthenticated(HttpContext httpContext)
    {
        return HasValidApiKeyHeader(httpContext) || HasValidSessionCookie(httpContext);
    }

    public bool IsApiKeyValid(string? suppliedApiKey)
    {
        if (string.IsNullOrEmpty(suppliedApiKey))
        {
            return false;
        }

        return FixedTimeEquals(_expectedApiKeyBytes, Encoding.UTF8.GetBytes(suppliedApiKey));
    }

    public string CreateSessionValue()
    {
        var expiresAtUtc = DateTimeOffset.UtcNow.Add(_sessionLifetime);
        return _dataProtector.Protect(expiresAtUtc.ToUnixTimeSeconds().ToString());
    }

    public void AppendSessionCookie(HttpResponse response, string sessionValue)
    {
        response.Cookies.Append(
            SessionCookieName,
            sessionValue,
            new CookieOptions
            {
                HttpOnly = true,
                IsEssential = true,
                Path = "/defense",
                SameSite = SameSiteMode.Strict,
                Secure = response.HttpContext.Request.IsHttps,
                MaxAge = _sessionLifetime
            });
    }

    public void ClearSessionCookie(HttpResponse response)
    {
        response.Cookies.Delete(
            SessionCookieName,
            new CookieOptions
            {
                Path = "/defense",
                SameSite = SameSiteMode.Strict,
                Secure = response.HttpContext.Request.IsHttps
            });
    }

    public bool HasValidApiKeyHeader(HttpContext httpContext)
    {
        if (!httpContext.Request.Headers.TryGetValue(_headerName, out var suppliedApiKey))
        {
            return false;
        }

        return IsApiKeyValid(suppliedApiKey.ToString());
    }

    public bool HasValidSessionCookie(HttpContext httpContext)
    {
        if (!httpContext.Request.Cookies.TryGetValue(SessionCookieName, out var sessionValue) ||
            string.IsNullOrWhiteSpace(sessionValue))
        {
            return false;
        }

        try
        {
            var payload = _dataProtector.Unprotect(sessionValue);
            if (!long.TryParse(payload, out var expiresAtUnixSeconds))
            {
                return false;
            }

            var expiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(expiresAtUnixSeconds);
            return expiresAtUtc > DateTimeOffset.UtcNow;
        }
        catch
        {
            return false;
        }
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
