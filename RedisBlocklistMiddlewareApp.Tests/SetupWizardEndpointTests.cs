using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class SetupWizardEndpointTests
{
    [Fact]
    public async Task ApiKeyFilter_RejectsCookieMutationFromForeignSameSiteOrigin()
    {
        var options = Options.Create(new DefenseEngineOptions
        {
            Management = new ManagementOptions { ApiKey = "test-management-key", DashboardSessionHours = 8 }
        });
        var auth = new ManagementAuthenticationService(
            options,
            DataProtectionProvider.Create("test"),
            new HostingEnvironment { EnvironmentName = "Development" });
        var filter = new ApiKeyEndpointFilter(auth);
        var sessionCookie = auth.CreateSessionValue();

        var foreignCtx = new DefaultHttpContext();
        foreignCtx.Request.Method = HttpMethods.Post;
        foreignCtx.Request.Scheme = "http";
        foreignCtx.Request.Host = new HostString("127.0.0.1", 5000);
        foreignCtx.Request.Headers.Cookie = $"{ManagementAuthenticationService.SessionCookieName}={sessionCookie}";
        foreignCtx.Request.Headers.Origin = "http://127.0.0.1:9999";
        foreignCtx.Request.Headers["Sec-Fetch-Site"] = "same-site";
        var foreignResult = await filter.InvokeAsync(
            new TestFilterContext(foreignCtx),
            _ => ValueTask.FromResult<object?>(Results.Ok()));
        Assert.Equal(StatusCodes.Status403Forbidden, ((IStatusCodeHttpResult)foreignResult!).StatusCode);

        var sameOriginCtx = new DefaultHttpContext();
        sameOriginCtx.Request.Method = HttpMethods.Post;
        sameOriginCtx.Request.Scheme = "http";
        sameOriginCtx.Request.Host = new HostString("127.0.0.1", 5000);
        sameOriginCtx.Request.Headers.Cookie = $"{ManagementAuthenticationService.SessionCookieName}={sessionCookie}";
        sameOriginCtx.Request.Headers.Origin = "http://127.0.0.1:5000";
        sameOriginCtx.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        var okResult = await filter.InvokeAsync(
            new TestFilterContext(sameOriginCtx),
            _ => ValueTask.FromResult<object?>(Results.Ok()));
        Assert.Equal(StatusCodes.Status200OK, ((IStatusCodeHttpResult)okResult!).StatusCode);

        // TLS-terminating Ingress: browser Origin is https://protected.example.com while pod sees http://protected.example.com.
        var tlsIngressCtx = new DefaultHttpContext();
        tlsIngressCtx.Request.Method = HttpMethods.Post;
        tlsIngressCtx.Request.Scheme = "http";
        tlsIngressCtx.Request.Host = new HostString("protected.example.com");
        tlsIngressCtx.Request.Headers.Cookie = $"{ManagementAuthenticationService.SessionCookieName}={sessionCookie}";
        tlsIngressCtx.Request.Headers.Origin = "https://protected.example.com";
        tlsIngressCtx.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        var ingressResult = await filter.InvokeAsync(
            new TestFilterContext(tlsIngressCtx),
            _ => ValueTask.FromResult<object?>(Results.Ok()));
        Assert.Equal(StatusCodes.Status200OK, ((IStatusCodeHttpResult)ingressResult!).StatusCode);
    }

    private sealed class TestFilterContext(HttpContext httpContext) : EndpointFilterInvocationContext
    {
        public override HttpContext HttpContext => httpContext;
        public override IList<object?> Arguments { get; } = [];
        public override T GetArgument<T>(int index) => (T)Arguments[index]!;
    }

    [Fact]
    public void SetupWizardService_UpdatesValidatesRuntimeOptionsAndAllowsRerun()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var options = Options.Create(new DefenseEngineOptions());
        var service = new SetupWizardService(config, options);

        Assert.False(service.GetState().Completed);
        Assert.Throws<ArgumentException>(() => service.Update(new SetupWizardUpdateRequest(
            BackendUrl: "ftp://invalid.example.com",
            ModelUri: null,
            CaptchaProvider: null,
            CloudflareEnabled: null,
            EnableMcpModel: null,
            MarkComplete: true)));

        Assert.Throws<ArgumentException>(() => service.Update(new SetupWizardUpdateRequest(
            BackendUrl: "https://origin.example.com",
            ModelUri: null,
            CaptchaProvider: "invalid-provider",
            CloudflareEnabled: null,
            EnableMcpModel: null,
            MarkComplete: true)));

        var first = service.Update(new SetupWizardUpdateRequest(
            BackendUrl: "https://origin.example.com",
            ModelUri: "openai://gpt-4o-mini",
            CaptchaProvider: "builtin",
            CloudflareEnabled: true,
            EnableMcpModel: false,
            MarkComplete: true));
        Assert.True(first.Completed);
        Assert.Equal("https://origin.example.com", first.BackendUrl);
        Assert.True(options.Value.Cloudflare.Enabled);
        Assert.False(options.Value.Escalation.McpModel.Enabled);
        Assert.Equal("openai://gpt-4o-mini", options.Value.Escalation.McpModel.ModelUri);

        // Authenticated operators can re-run setup anytime after completion.
        var rerun = service.Update(new SetupWizardUpdateRequest(
            BackendUrl: "https://origin2.example.com",
            ModelUri: "mcp://primary/classify",
            CaptchaProvider: "turnstile",
            CloudflareEnabled: false,
            EnableMcpModel: true,
            MarkComplete: true));
        Assert.Equal("https://origin2.example.com", rerun.BackendUrl);
        Assert.Equal("turnstile", rerun.CaptchaProvider);
        Assert.True(rerun.EnableMcpModel);
        Assert.False(options.Value.Cloudflare.Enabled);
        Assert.True(options.Value.Escalation.McpModel.Enabled);
        Assert.Equal("mcp://primary/classify", options.Value.Escalation.McpModel.ModelUri);
        Assert.Contains("First-Run Setup Wizard", service.RenderHtml(), StringComparison.Ordinal);
    }
}
