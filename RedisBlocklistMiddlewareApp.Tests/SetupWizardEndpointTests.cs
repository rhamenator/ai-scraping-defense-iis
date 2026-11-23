using Microsoft.Extensions.Configuration;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class SetupWizardEndpointTests
{
    [Fact]
    public void SetupWizardService_UpdatesValidatesAndAllowsRerun()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var service = new SetupWizardService(config);

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

        // Authenticated operators can re-run setup anytime after completion.
        var rerun = service.Update(new SetupWizardUpdateRequest(
            BackendUrl: "https://origin2.example.com",
            ModelUri: null,
            CaptchaProvider: "turnstile",
            CloudflareEnabled: false,
            EnableMcpModel: true,
            MarkComplete: true));
        Assert.Equal("https://origin2.example.com", rerun.BackendUrl);
        Assert.Equal("turnstile", rerun.CaptchaProvider);
        Assert.True(rerun.EnableMcpModel);
        Assert.Contains("First-Run Setup Wizard", service.RenderHtml(), StringComparison.Ordinal);
    }
}
