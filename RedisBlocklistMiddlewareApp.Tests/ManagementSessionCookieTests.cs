using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class ManagementSessionCookieTests
{
    [Theory]
    [InlineData("Production", false, true)]   // behind a TLS-terminating ingress the app sees HTTP: still Secure
    [InlineData("Production", true, true)]
    [InlineData("Staging", false, true)]
    [InlineData("Development", true, true)]
    [InlineData("Development", false, false)] // local development over plain HTTP keeps working
    public void SessionCookie_IsSecure_OutsideDevelopmentOrOverHttps(string environment, bool isHttps, bool expectSecure)
    {
        var service = CreateService(environment);
        var context = new DefaultHttpContext();
        context.Request.Scheme = isHttps ? "https" : "http";

        service.AppendSessionCookie(context.Response, service.CreateSessionValue());

        var setCookie = context.Response.Headers.SetCookie.ToString();
        Assert.Equal(expectSecure, setCookie.Contains("secure", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ClearedSessionCookie_IsSecureInProduction()
    {
        var service = CreateService("Production");
        var context = new DefaultHttpContext();

        service.ClearSessionCookie(context.Response);

        Assert.Contains("secure", context.Response.Headers.SetCookie.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static ManagementAuthenticationService CreateService(string environmentName)
    {
        var options = new DefenseEngineOptions();
        options.Management.ApiKey = "management-key-for-unit-tests-only-1234567890";
        return new ManagementAuthenticationService(
            Options.Create(options),
            new EphemeralDataProtectionProvider(),
            new TestHostEnvironment(environmentName));
    }

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "RedisBlocklistMiddlewareApp.Tests";

        public string ContentRootPath { get; set; } = Path.GetTempPath();

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
