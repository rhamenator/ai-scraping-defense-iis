using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class SetupWizardEndpointTests
{
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
