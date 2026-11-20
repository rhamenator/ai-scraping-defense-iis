using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class RequestSignalEvaluatorTests
{
    [Fact]
    public void Evaluate_BlocksImmediately_ForKnownBadUserAgent()
    {
        var evaluator = CreateEvaluator();
        var context = CreateContext("/products", userAgent: "Mozilla/5.0 GPTBot/1.0");

        var result = evaluator.Evaluate(context);

        Assert.True(result.BlockImmediately);
        Assert.Equal("known_bad_user_agent", result.BlockReason);
        Assert.Contains("known_bad_user_agent:GPTBot", result.Signals);
    }

    [Fact]
    public void Evaluate_AddsExpectedSignals_ForSuspiciousAnonymousRequest()
    {
        var evaluator = CreateEvaluator();
        var context = CreateContext(
            "/wp-admin/export",
            userAgent: string.Empty,
            accept: "*/*",
            includeAcceptLanguage: false,
            queryString: "?" + new string('a', 201));

        var result = evaluator.Evaluate(context);

        Assert.False(result.BlockImmediately);
        Assert.Equal(
            [
                "empty_user_agent",
                "missing_accept_language",
                "generic_accept_any",
                "suspicious_path:/wp-admin",
                "long_query_string"
            ],
            result.Signals);
    }

    [Fact]
    public void Evaluate_RespectsDisabledChecks()
    {
        var evaluator = CreateEvaluator(options =>
        {
            options.Heuristics.CheckEmptyUserAgent = false;
            options.Heuristics.CheckMissingAcceptLanguage = false;
            options.Heuristics.CheckGenericAcceptHeader = false;
        });
        var context = CreateContext(
            "/landing",
            userAgent: string.Empty,
            accept: "*/*",
            includeAcceptLanguage: false);

        var result = evaluator.Evaluate(context);

        Assert.Empty(result.Signals);
    }

    [Theory]
    [InlineData("curl/8.5.0")]
    [InlineData("python-requests/2.32.3")]
    [InlineData("Wget/1.21.4")]
    public void Evaluate_ScoresButDoesNotBlock_GenericHttpClients(string userAgent)
    {
        var result = CreateEvaluator().Evaluate(CreateContext("/pricing", userAgent));

        Assert.False(result.BlockImmediately);
        Assert.Contains(result.Signals, signal => signal.StartsWith("generic_http_client:", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("sqlmap/1.7")]
    [InlineData("Nikto/2.5.0")]
    [InlineData("masscan/1.3")]
    [InlineData("GPTBot/1.2")]
    public void Evaluate_StillBlocksImmediately_OffensiveToolsAndNamedAiCrawlers(string userAgent)
    {
        var result = CreateEvaluator().Evaluate(CreateContext("/pricing", userAgent));

        Assert.True(result.BlockImmediately);
        Assert.Equal("known_bad_user_agent", result.BlockReason);
    }

    [Fact]
    public void Evaluate_BlocksGenericClients_WhenOperatorOptsIn()
    {
        var evaluator = CreateEvaluator(options => options.Heuristics.BlockGenericClients = true);

        var result = evaluator.Evaluate(CreateContext("/pricing", "curl/8.5.0"));

        Assert.True(result.BlockImmediately);
        Assert.Equal("generic_http_client", result.BlockReason);
    }

    [Fact]
    public void Evaluate_DoesNotTreatGooglebotOrBingbotAsKnownBad()
    {
        var evaluator = CreateEvaluator();

        Assert.False(evaluator.Evaluate(CreateContext("/", "Mozilla/5.0 (compatible; Googlebot/2.1)")).BlockImmediately);
        Assert.False(evaluator.Evaluate(CreateContext("/", "Mozilla/5.0 (compatible; bingbot/2.0)")).BlockImmediately);
    }

    [Theory]
    [InlineData("/wp-admin/setup.php")]
    [InlineData("/.env")]
    [InlineData("/admin/")]
    public void Evaluate_BlocksImmediately_WhenARobotsDisallowedPathIsRequested(string path)
    {
        var evaluator = CreateEvaluatorWithRobots("User-agent: *\nDisallow: /wp-admin/\nDisallow: /.env\nDisallow: /admin/\n");

        var result = evaluator.Evaluate(CreateContext(path, BrowserUserAgent));

        Assert.True(result.BlockImmediately);
        Assert.Equal("robots_violation", result.BlockReason);
        Assert.StartsWith("robots_violation:Disallow: ", Assert.Single(result.Signals), StringComparison.Ordinal);
    }

    [Fact]
    public void Evaluate_DoesNotExemptAClaimedCrawler_FromRobotsEnforcement()
    {
        var evaluator = CreateEvaluatorWithRobots("User-agent: *\nDisallow: /private/\n");

        var result = evaluator.Evaluate(CreateContext("/private/a", "Mozilla/5.0 (compatible; Googlebot/2.1)"));

        Assert.True(result.BlockImmediately);
        Assert.Equal("robots_violation", result.BlockReason);
    }

    [Fact]
    public void Evaluate_AllowsPermittedPathsAndRobotsTxtItself()
    {
        var evaluator = CreateEvaluatorWithRobots("User-agent: *\nDisallow: /\nAllow: /public/\n");

        Assert.False(evaluator.Evaluate(CreateContext("/public/page", BrowserUserAgent)).BlockImmediately);
        Assert.False(evaluator.Evaluate(CreateContext("/robots.txt", BrowserUserAgent)).BlockImmediately);
    }

    [Fact]
    public void Evaluate_RobotsViolationIsCheckedBeforeTheUserAgent()
    {
        var evaluator = CreateEvaluatorWithRobots("User-agent: *\nDisallow: /private/\n");

        var result = evaluator.Evaluate(CreateContext("/private/a", "curl/8.5.0"));

        Assert.Equal("robots_violation", result.BlockReason);
    }

    private const string BrowserUserAgent =
        "Mozilla/5.0 (X11; Linux x86_64; rv:128.0) Gecko/20100101 Firefox/128.0";

    private static RequestSignalEvaluator CreateEvaluatorWithRobots(string robotsText)
    {
        return new RequestSignalEvaluator(
            Options.Create(new DefenseEngineOptions()),
            new StaticRobotsPolicyProvider(robotsText));
    }

    private sealed class StaticRobotsPolicyProvider : IRobotsPolicyProvider
    {
        public StaticRobotsPolicyProvider(string text)
        {
            Policy = RobotsPolicy.Parse(text);
        }

        public RobotsPolicy Policy { get; }

        public string? ServedText => null;
    }

    private static RequestSignalEvaluator CreateEvaluator(Action<DefenseEngineOptions>? configure = null)
    {
        var options = new DefenseEngineOptions();
        configure?.Invoke(options);
        return new RequestSignalEvaluator(Options.Create(options));
    }

    private static DefaultHttpContext CreateContext(
        string path,
        string userAgent,
        string accept = "text/html",
        bool includeAcceptLanguage = true,
        string queryString = "")
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(queryString);
        context.Request.Headers.UserAgent = userAgent;
        context.Request.Headers.Accept = accept;

        if (includeAcceptLanguage)
        {
            context.Request.Headers.AcceptLanguage = "en-US";
        }

        return context;
    }
}
