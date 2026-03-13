using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class TarpitPageServiceTests
{
    [Fact]
    public void GeneratePage_IsDeterministic_ForSamePathAndClientIp()
    {
        var service = CreateService();

        var first = service.GeneratePage("archive/index", "198.51.100.50");
        var second = service.GeneratePage("archive/index", "198.51.100.50");

        Assert.Equal(first, second);
    }

    [Fact]
    public void GeneratePage_HtmlEncodesDisplayedPath()
    {
        var service = CreateService();

        var page = service.GeneratePage("<script>alert(1)</script>", "198.51.100.50");

        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", page);
        Assert.DoesNotContain("Session path: /<script>alert(1)</script>", page);
    }

    [Fact]
    public void GeneratePage_UsesConfiguredTarpitPrefixInLinks()
    {
        var service = CreateService(options =>
        {
            options.Tarpit.PathPrefix = "/custom-tarpit";
            options.Tarpit.LinkCount = 2;
            options.Tarpit.ParagraphCount = 1;
        });

        var page = service.GeneratePage("nested/path", "198.51.100.50");

        Assert.Contains("href=\"/custom-tarpit/nested/path/", page);
    }

    private static TarpitPageService CreateService(Action<DefenseEngineOptions>? configure = null)
    {
        var options = new DefenseEngineOptions();
        configure?.Invoke(options);
        return new TarpitPageService(Options.Create(options));
    }
}
