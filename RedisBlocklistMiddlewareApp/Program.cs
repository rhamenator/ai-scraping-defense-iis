using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Services;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<DefenseEngineOptions>()
    .Bind(builder.Configuration.GetSection(DefenseEngineOptions.SectionName))
    .PostConfigure(options =>
    {
        var connectionString = builder.Configuration.GetConnectionString("RedisConnection");
        if (!string.IsNullOrWhiteSpace(connectionString) &&
            string.IsNullOrWhiteSpace(options.Redis.ConnectionString))
        {
            options.Redis.ConnectionString = connectionString;
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

builder.Services.AddSingleton<IConnectionMultiplexer>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<Program>>();
    var options = sp.GetRequiredService<IOptions<DefenseEngineOptions>>().Value;
    var redisConnectionString = options.Redis.ConnectionString;

    if (string.IsNullOrWhiteSpace(redisConnectionString))
    {
        throw new InvalidOperationException("Redis connection string is not configured.");
    }

    logger.LogInformation(
        "Connecting to Redis at {RedisHost}",
        redisConnectionString.Split(',')[0]);

    return ConnectionMultiplexer.Connect(redisConnectionString);
});

builder.Services.AddSingleton<IBlocklistService, RedisBlocklistService>();
builder.Services.AddSingleton<IRequestFrequencyTracker, RedisRequestFrequencyTracker>();
builder.Services.AddSingleton<IDefenseEventStore, DefenseEventStore>();
builder.Services.AddSingleton<ISuspiciousRequestQueue, SuspiciousRequestQueue>();
builder.Services.AddSingleton<IRequestSignalEvaluator, RequestSignalEvaluator>();
builder.Services.AddSingleton<ITarpitPageService, TarpitPageService>();
builder.Services.AddHostedService<DefenseAnalysisService>();

var app = builder.Build();

app.UseMiddleware<RedisBlocklistMiddleware>();

app.MapGet("/", () => Results.Ok(new
{
    service = "ai-scraping-defense-dotnet",
    mode = "foundation",
    endpoints = new
    {
        health = "/health",
        events = "/defense/events",
        tarpit = "/anti-scrape-tarpit/{path}"
    }
}));

app.MapGet("/health", async (
    IConnectionMultiplexer redis,
    IOptions<DefenseEngineOptions> options) =>
{
    try
    {
        await redis.GetDatabase(options.Value.Redis.BlocklistDatabase).PingAsync();
        return Results.Ok(new { status = "healthy" });
    }
    catch (Exception ex)
    {
        return Results.Json(
            new
            {
                status = "degraded",
                error = ex.Message
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

app.MapGet("/anti-scrape-tarpit/{**path}", async (
    HttpContext context,
    string? path,
    ITarpitPageService tarpitPageService,
    IOptions<DefenseEngineOptions> options,
    CancellationToken cancellationToken) =>
{
    var tarpitOptions = options.Value.Tarpit;

    if (tarpitOptions.ResponseDelayMilliseconds > 0)
    {
        await Task.Delay(tarpitOptions.ResponseDelayMilliseconds, cancellationToken);
    }

    var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    var content = tarpitPageService.GeneratePage(path ?? string.Empty, clientIp);
    return Results.Content(content, "text/html");
});

app.Run(async context =>
{
    context.Response.StatusCode = StatusCodes.Status404NotFound;
    await context.Response.WriteAsync("Endpoint not found.");
});

app.Run();
