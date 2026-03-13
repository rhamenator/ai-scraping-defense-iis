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
