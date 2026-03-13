using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;
using System.Net;

var builder = WebApplication.CreateBuilder(args);
var redisConnectionString = builder.Configuration.GetConnectionString("RedisConnection");

builder.Services.AddSingleton<IValidateOptions<DefenseEngineOptions>, DefenseEngineOptionsValidator>();
builder.Services.AddSingleton<ProductionConfigurationValidator>();
builder.Services
    .AddOptions<DefenseEngineOptions>()
    .Bind(builder.Configuration.GetSection(DefenseEngineOptions.SectionName))
    .ValidateOnStart()
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

        options.Management.ApiKeyHeaderName = string.IsNullOrWhiteSpace(options.Management.ApiKeyHeaderName)
            ? "X-API-Key"
            : options.Management.ApiKeyHeaderName.Trim();

        options.Management.ApiKey = options.Management.ApiKey.Trim();

        options.Intake.ApiKeyHeaderName = string.IsNullOrWhiteSpace(options.Intake.ApiKeyHeaderName)
            ? "X-Webhook-Key"
            : options.Intake.ApiKeyHeaderName.Trim();

        options.Intake.ApiKey = options.Intake.ApiKey.Trim();

        options.Networking.ClientIpResolutionMode = string.IsNullOrWhiteSpace(options.Networking.ClientIpResolutionMode)
            ? ClientIpResolutionModes.Direct
            : options.Networking.ClientIpResolutionMode.Trim();

        options.Networking.TrustedProxies = options.Networking.TrustedProxies
            .Where(proxy => !string.IsNullOrWhiteSpace(proxy))
            .Select(proxy => proxy.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        options.Audit.DatabasePath = string.IsNullOrWhiteSpace(options.Audit.DatabasePath)
            ? "data/defense-events.db"
            : options.Audit.DatabasePath.Trim();

        options.Audit.MaxRecentEvents = Math.Max(1, options.Audit.MaxRecentEvents);

        if (!options.Redis.BlocklistKeyPrefix.EndsWith(':'))
        {
            options.Redis.BlocklistKeyPrefix += ":";
        }

        if (!options.Redis.FrequencyKeyPrefix.EndsWith(':'))
        {
            options.Redis.FrequencyKeyPrefix += ":";
        }
    });

builder.Services.AddSingleton<IConfigureOptions<ForwardedHeadersOptions>, ForwardedHeadersOptionsSetup>();

builder.Services.AddSingleton<IRedisConnectionProvider, RedisConnectionProvider>();
builder.Services.AddSingleton<IBlocklistService, RedisBlocklistService>();
builder.Services.AddSingleton<IRequestFrequencyTracker, RedisRequestFrequencyTracker>();
builder.Services.AddSingleton<IDefenseEventStore, SqliteDefenseEventStore>();
builder.Services.AddSingleton<ISuspiciousRequestQueue, SuspiciousRequestQueue>();
builder.Services.AddSingleton<IRequestSignalEvaluator, RequestSignalEvaluator>();
builder.Services.AddSingleton<ITarpitPageService, TarpitPageService>();
builder.Services.AddSingleton<IClientIpResolver, ClientIpResolver>();
builder.Services.AddSingleton<ApiKeyEndpointFilter>();
builder.Services.AddSingleton<IntakeApiKeyEndpointFilter>();
builder.Services.AddSingleton<IWebhookEventInbox, SqliteWebhookEventInbox>();
builder.Services.AddHostedService<StartupValidationService>();
builder.Services.AddHostedService<DefenseAnalysisService>();
builder.Services.AddHostedService<WebhookIntakeProcessingService>();

var app = builder.Build();
var runtimeOptions = app.Services.GetRequiredService<IOptions<DefenseEngineOptions>>().Value;
var tarpitRoutePattern = Program.GetTarpitRoutePattern(runtimeOptions);
var advertisedEndpoints = Program.GetAdvertisedEndpoints(runtimeOptions);

if (Program.ShouldUseForwardedHeaders(runtimeOptions))
{
    app.UseForwardedHeaders();
}

app.UseMiddleware<RedisBlocklistMiddleware>();

app.MapGet("/", () => Results.Ok(new
{
    service = "ai-scraping-defense-dotnet",
    mode = "foundation",
    endpoints = advertisedEndpoints
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

Program.MapManagementEndpoints(app, runtimeOptions);
Program.MapIntakeEndpoints(app, runtimeOptions);

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

public partial class Program
{
    public static IReadOnlyDictionary<string, string> GetAdvertisedEndpoints(DefenseEngineOptions runtimeOptions)
    {
        var endpoints = new Dictionary<string, string>
        {
            ["health"] = "/health",
            ["tarpit"] = $"{runtimeOptions.Tarpit.PathPrefix}/{{path}}"
        };

        if (ShouldExposeManagementEndpoints(runtimeOptions))
        {
            endpoints["events"] = "/defense/events";
            endpoints["metrics"] = "/defense/metrics";
        }

        if (ShouldExposeIntakeEndpoints(runtimeOptions))
        {
            endpoints["analyze"] = "/analyze";
        }

        return endpoints;
    }

    public static void MapManagementEndpoints(
        IEndpointRouteBuilder app,
        DefenseEngineOptions runtimeOptions)
    {
        if (!ShouldExposeManagementEndpoints(runtimeOptions))
        {
            return;
        }

        var management = app.MapGroup("/defense")
            .AddEndpointFilter<ApiKeyEndpointFilter>();

        management.MapGet("/events", (
            IDefenseEventStore store,
            int count = 50) =>
        {
            return Results.Ok(store.GetRecent(count));
        });

        management.MapGet("/metrics", (
            IDefenseEventStore store) =>
        {
            return Results.Ok(store.GetMetrics());
        });

        management.MapGet("/blocklist", async (
            [FromQuery] string ip,
            IBlocklistService blocklistService,
            CancellationToken cancellationToken) =>
        {
            if (!TryNormalizeIpAddress(ip, out var normalizedIp))
            {
                return Results.BadRequest(new
                {
                    error = "The ip query parameter must contain a valid IPv4 or IPv6 address."
                });
            }

            return Results.Ok(new
            {
                ip = normalizedIp,
                blocked = await blocklistService.IsBlockedAsync(normalizedIp, cancellationToken)
            });
        });

        management.MapPost("/blocklist", async (
            [FromQuery] string ip,
            [FromQuery] string? reason,
            IBlocklistService blocklistService,
            CancellationToken cancellationToken) =>
        {
            if (!TryNormalizeIpAddress(ip, out var normalizedIp))
            {
                return Results.BadRequest(new
                {
                    error = "The ip query parameter must contain a valid IPv4 or IPv6 address."
                });
            }

            await blocklistService.BlockAsync(
                normalizedIp,
                string.IsNullOrWhiteSpace(reason) ? "manual_block" : reason.Trim(),
                ["manual_block"],
                cancellationToken);

            return Results.Accepted($"/defense/blocklist?ip={Uri.EscapeDataString(normalizedIp)}", new
            {
                ip = normalizedIp,
                blocked = true
            });
        });

        management.MapDelete("/blocklist", async (
            [FromQuery] string ip,
            IBlocklistService blocklistService,
            CancellationToken cancellationToken) =>
        {
            if (!TryNormalizeIpAddress(ip, out var normalizedIp))
            {
                return Results.BadRequest(new
                {
                    error = "The ip query parameter must contain a valid IPv4 or IPv6 address."
                });
            }

            await blocklistService.UnblockAsync(normalizedIp, cancellationToken);
            return Results.Ok(new
            {
                ip = normalizedIp,
                blocked = false
            });
        });
    }

    public static bool ShouldExposeManagementEndpoints(DefenseEngineOptions runtimeOptions)
    {
        return !string.IsNullOrWhiteSpace(runtimeOptions.Management.ApiKey);
    }

    public static bool ShouldUseForwardedHeaders(DefenseEngineOptions runtimeOptions)
    {
        return string.Equals(
            runtimeOptions.Networking.ClientIpResolutionMode,
            ClientIpResolutionModes.TrustedProxy,
            StringComparison.OrdinalIgnoreCase);
    }

    public static void MapIntakeEndpoints(
        IEndpointRouteBuilder app,
        DefenseEngineOptions runtimeOptions)
    {
        if (!ShouldExposeIntakeEndpoints(runtimeOptions))
        {
            return;
        }

        app.MapPost("/analyze", async (
            IntakeWebhookEvent webhookEvent,
            IWebhookEventInbox inbox,
            CancellationToken cancellationToken) =>
        {
            if (!TryNormalizeIpAddress(webhookEvent.Details.IpAddress, out var normalizedIp))
            {
                return Results.BadRequest(new
                {
                    error = "The webhook details.ip field must contain a valid IPv4 or IPv6 address."
                });
            }

            var normalizedEvent = webhookEvent with
            {
                Details = webhookEvent.Details with
                {
                    IpAddress = normalizedIp
                }
            };

            var itemId = await inbox.EnqueueAsync(normalizedEvent, cancellationToken);

            return Results.Accepted($"/analyze/{itemId}", new
            {
                status = "queued",
                itemId
            });
        })
        .AddEndpointFilter<IntakeApiKeyEndpointFilter>();
    }

    public static string GetTarpitRoutePattern(DefenseEngineOptions runtimeOptions)
    {
        return $"{runtimeOptions.Tarpit.PathPrefix}/{{**path}}";
    }

    public static bool ShouldExposeIntakeEndpoints(DefenseEngineOptions runtimeOptions)
    {
        return !string.IsNullOrWhiteSpace(runtimeOptions.Intake.ApiKey);
    }

    public static bool TryNormalizeIpAddress(string value, out string normalizedIp)
    {
        normalizedIp = string.Empty;

        if (!IPAddress.TryParse(value, out var address))
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        normalizedIp = address.ToString();
        return true;
    }
}
