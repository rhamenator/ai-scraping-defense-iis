using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Services;
using System.Net;

var builder = WebApplication.CreateBuilder(args);
var redisConnectionString = builder.Configuration.GetConnectionString("RedisConnection");
var trustedProxyAddresses = builder.Configuration
    .GetSection($"{DefenseEngineOptions.SectionName}:Networking:TrustedProxies")
    .Get<string[]>() ?? [];

builder.Services
    .AddOptions<DefenseEngineOptions>()
    .Bind(builder.Configuration.GetSection(DefenseEngineOptions.SectionName))
    .PostConfigure(options =>
    {
        if (!string.IsNullOrWhiteSpace(redisConnectionString) &&
            string.IsNullOrWhiteSpace(options.Redis.ConnectionString))
        {
            options.Redis.ConnectionString = redisConnectionString;
        }

        if (!options.Tarpit.PathPrefix.StartsWith('/'))
        {
            options.Tarpit.PathPrefix = "/" + options.Tarpit.PathPrefix;
        }

        options.Tarpit.PathPrefix = options.Tarpit.PathPrefix.TrimEnd('/');

        if (!options.Redis.BlocklistKeyPrefix.EndsWith(':'))
        {
            options.Redis.BlocklistKeyPrefix += ":";
        }

        if (!options.Redis.FrequencyKeyPrefix.EndsWith(':'))
        {
            options.Redis.FrequencyKeyPrefix += ":";
        }
    });

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    options.ForwardLimit = 1;
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();

    foreach (var trustedProxyAddress in trustedProxyAddresses)
    {
        if (IPAddress.TryParse(trustedProxyAddress, out var trustedProxy))
        {
            options.KnownProxies.Add(trustedProxy);
        }
    }
});

builder.Services.AddSingleton<IRedisConnectionProvider, RedisConnectionProvider>();
builder.Services.AddSingleton<IBlocklistService, RedisBlocklistService>();
builder.Services.AddSingleton<IRequestFrequencyTracker, RedisRequestFrequencyTracker>();
builder.Services.AddSingleton<IDefenseEventStore, DefenseEventStore>();
builder.Services.AddSingleton<ISuspiciousRequestQueue, SuspiciousRequestQueue>();
builder.Services.AddSingleton<IRequestSignalEvaluator, RequestSignalEvaluator>();
builder.Services.AddSingleton<ITarpitPageService, TarpitPageService>();
builder.Services.AddSingleton<IClientIpResolver, ClientIpResolver>();
builder.Services.AddHostedService<DefenseAnalysisService>();

var app = builder.Build();
var runtimeOptions = app.Services.GetRequiredService<IOptions<DefenseEngineOptions>>().Value;
var tarpitRoutePattern = $"{runtimeOptions.Tarpit.PathPrefix}/{{**path}}";

app.UseForwardedHeaders();
app.UseMiddleware<RedisBlocklistMiddleware>();

app.MapGet("/", () => Results.Ok(new
{
    service = "ai-scraping-defense-dotnet",
    mode = "foundation",
    endpoints = new
    {
        health = "/health",
        events = "/defense/events",
        tarpit = $"{runtimeOptions.Tarpit.PathPrefix}/{{path}}"
    }
}));

app.MapGet("/health", async (
    IRedisConnectionProvider redisConnectionProvider,
    IOptions<DefenseEngineOptions> options,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    try
    {
        var redis = await redisConnectionProvider.GetAsync(cancellationToken);
        await redis.GetDatabase(options.Value.Redis.BlocklistDatabase).PingAsync();
        return Results.Ok(new { status = "healthy" });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Redis health check failed.");
        return Results.Json(
            new
            {
                status = "degraded",
                error = "A dependency check failed."
            },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.MapGet("/defense/events", (
    IDefenseEventStore store,
    int count = 50) =>
{
    return Results.Ok(store.GetRecent(count));
});

app.MapGet(tarpitRoutePattern, async (
    HttpContext context,
    string? path,
    ITarpitPageService tarpitPageService,
    IClientIpResolver clientIpResolver,
    IOptions<DefenseEngineOptions> options,
    CancellationToken cancellationToken) =>
{
    var tarpitOptions = options.Value.Tarpit;

    if (tarpitOptions.ResponseDelayMilliseconds > 0)
    {
        await Task.Delay(tarpitOptions.ResponseDelayMilliseconds, cancellationToken);
    }

    var clientIp = clientIpResolver.Resolve(context) ?? "unknown";
    var content = tarpitPageService.GeneratePage(path ?? string.Empty, clientIp);
    return Results.Content(content, "text/html");
});

app.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status404NotFound;
    await context.Response.WriteAsync("Endpoint not found.");
});

app.Run();
