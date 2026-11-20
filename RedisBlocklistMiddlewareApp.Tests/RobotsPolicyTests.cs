using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class RobotsPolicyTests
{
    private const string Star = "User-agent: *\n";

    [Theory]
    [InlineData("Disallow: /api/metrics # comment\n", "/api/metrics", true)]
    [InlineData("Disallow: /static/ # comment\n", "/static/app.js", true)]
    [InlineData("Disallow: /Admin/\n", "/Admin/", true)]
    [InlineData("Disallow: /Admin/\n", "/admin/", false)]
    [InlineData("Disallow: /admin/\n", "/%61dmin/", true)]
    [InlineData("Disallow: /%61dmin/\n", "/admin/", true)]
    [InlineData("Disallow: /a%2fb\n", "/a%2Fb", true)]
    [InlineData("Disallow: /a%2fb\n", "/a/b", false)]
    [InlineData("Disallow: /\n", "/anything", true)]
    [InlineData("Disallow: /\n", "/robots.txt", false)]
    [InlineData("Disallow: /\n", "/robots.txt?v=1", false)]
    [InlineData("Disallow: /\nAllow: /public/\n", "/public/page", false)]
    [InlineData("Disallow: /\nAllow: /public/\n", "/private", true)]
    [InlineData("Allow: /same\nDisallow: /same\n", "/same", false)]
    [InlineData("Disallow: /same\nAllow: /same\n", "/same", false)]
    [InlineData("Disallow:\n", "/anything", false)]
    [InlineData("Disallow: /*.json$\n", "/data.json", true)]
    [InlineData("Disallow: /*.json$\n", "/data.json.bak", false)]
    [InlineData("Disallow: /search?*\n", "/search?q=1", true)]
    public void IsDisallowed_FollowsRfc9309(string rules, string path, bool expected)
    {
        var policy = RobotsPolicy.Parse(Star + rules);

        Assert.Equal(expected, policy.IsDisallowed(path, "ExampleBot/1.0"));
    }

    [Theory]
    [InlineData("Allow: /private\nDisallow: /private$\n")]
    [InlineData("Disallow: /private$\nAllow: /private\n")]
    public void AnchoredRule_OutranksUnanchoredRuleOfTheSameText(string rules)
    {
        var policy = RobotsPolicy.Parse(Star + rules);

        Assert.True(policy.IsDisallowed("/private", "Mozilla/5.0"));
        Assert.False(policy.IsDisallowed("/private/doc", "Mozilla/5.0"));
    }

    [Fact]
    public void AnchoredAllow_OutranksUnanchoredDisallow()
    {
        var policy = RobotsPolicy.Parse(Star + "Disallow: /private\nAllow: /private$\n");

        Assert.False(policy.IsDisallowed("/private", "Mozilla/5.0"));
        Assert.True(policy.IsDisallowed("/private/doc", "Mozilla/5.0"));
    }

    [Fact]
    public void WildcardAndAnchor_KeepSpecificity()
    {
        var policy = RobotsPolicy.Parse(Star + "Allow: /a*\nDisallow: /a*b$\n");

        Assert.True(policy.IsDisallowed("/axb", "Mozilla/5.0"));
        Assert.False(policy.IsDisallowed("/axbc", "Mozilla/5.0"));
    }

    [Fact]
    public void SpecificUserAgentGroup_ReplacesTheWildcardGroup()
    {
        var policy = RobotsPolicy.Parse("User-agent: *\nDisallow: /\n\nUser-agent: examplebot\nDisallow: /private/\n");

        Assert.False(policy.IsDisallowed("/public", "Mozilla/5.0 (compatible; ExampleBot/1.0)"));
        Assert.True(policy.IsDisallowed("/private/x", "Mozilla/5.0 (compatible; ExampleBot/1.0)"));
        Assert.True(policy.IsDisallowed("/public", "Mozilla/5.0 Firefox/128.0"));
    }

    [Fact]
    public void UserAgentToken_MustMatchWholeToken()
    {
        var policy = RobotsPolicy.Parse("User-agent: *\nAllow: /\n\nUser-agent: bot\nDisallow: /\n");

        Assert.False(policy.IsDisallowed("/x", "Googlebot/2.1"));
        Assert.True(policy.IsDisallowed("/x", "bot/1.0"));
    }

    [Fact]
    public void ByteOrderMark_IsIgnored()
    {
        var policy = RobotsPolicy.Parse("﻿User-agent: *\nDisallow: /secret\n");

        Assert.True(policy.IsDisallowed("/secret", "x"));
    }

    [Fact]
    public void Empty_DisallowsNothing()
    {
        Assert.False(RobotsPolicy.Empty.IsDisallowed("/admin", "x"));
        Assert.False(RobotsPolicy.Empty.HasRules);
    }

    [Fact]
    public void DescribeMatchingRule_NamesTheRule_ForTheAuditTrail()
    {
        var policy = RobotsPolicy.Parse(Star + "Disallow: /admin/\n");

        Assert.Equal("Disallow: /admin/", policy.DescribeMatchingRule("/admin/users", "Mozilla/5.0"));
        Assert.Null(policy.DescribeMatchingRule("/pricing", "Mozilla/5.0"));
    }

    [Fact]
    public void ShippedDefaultPolicy_DisallowsSensitivePathsButNotOrdinaryPages()
    {
        var provider = new RobotsPolicyProvider(Options.Create(new DefenseEngineOptions()));

        Assert.True(provider.Policy.HasRules);
        Assert.True(provider.Policy.IsDisallowed("/wp-admin/setup.php", "Mozilla/5.0"));
        Assert.True(provider.Policy.IsDisallowed("/.env", "Mozilla/5.0"));
        Assert.True(provider.Policy.IsDisallowed("/anti-scrape-tarpit/a", "Mozilla/5.0"));
        Assert.False(provider.Policy.IsDisallowed("/pricing", "Mozilla/5.0"));
        Assert.NotNull(provider.ServedText);
    }

    [Fact]
    public void Provider_FailsStartup_WhenTheConfiguredFileIsMissing()
    {
        var options = new DefenseEngineOptions();
        options.Robots.PolicyPath = $"{Path.GetTempPath()}{Guid.NewGuid():N}{Path.DirectorySeparatorChar}robots.txt";

        Assert.Throws<InvalidOperationException>(() => new RobotsPolicyProvider(Options.Create(options)));
    }

    [Fact]
    public void Provider_IsEmpty_WhenRobotsEnforcementIsDisabled()
    {
        var options = new DefenseEngineOptions();
        options.Robots.Enabled = false;
        options.Robots.PolicyPath = "does-not-exist.txt";

        var provider = new RobotsPolicyProvider(Options.Create(options));

        Assert.False(provider.Policy.HasRules);
        Assert.Null(provider.ServedText);
    }

    [Fact]
    public void Provider_DoesNotServeThePolicy_WhenServingIsDisabled()
    {
        var options = new DefenseEngineOptions();
        options.Robots.ServePolicy = false;

        var provider = new RobotsPolicyProvider(Options.Create(options));

        Assert.True(provider.Policy.HasRules);
        Assert.Null(provider.ServedText);
    }
}
