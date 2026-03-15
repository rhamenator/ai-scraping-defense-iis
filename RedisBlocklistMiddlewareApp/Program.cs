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

builder.Services.AddHttpClient();
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
        options.Tarpit.Modes = options.Tarpit.Modes
            .Where(mode => !string.IsNullOrWhiteSpace(mode))
            .Select(mode => mode.Trim())
            .Where(mode =>
                string.Equals(mode, TarpitRenderModes.Standard, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, TarpitRenderModes.ArchiveIndex, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mode, TarpitRenderModes.ApiCatalog, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (options.Tarpit.Modes.Length == 0)
        {
            options.Tarpit.Modes = [TarpitRenderModes.Standard];
        }

        options.Tarpit.MarkovWordsPerParagraph = Math.Max(8, options.Tarpit.MarkovWordsPerParagraph);
        options.Tarpit.PostgresMarkov.ConnectionString = options.Tarpit.PostgresMarkov.ConnectionString.Trim();
        options.Tarpit.PostgresMarkov.WordsTableName = string.IsNullOrWhiteSpace(options.Tarpit.PostgresMarkov.WordsTableName)
            ? "markov_words"
            : options.Tarpit.PostgresMarkov.WordsTableName.Trim();
        options.Tarpit.PostgresMarkov.SequencesTableName = string.IsNullOrWhiteSpace(options.Tarpit.PostgresMarkov.SequencesTableName)
            ? "markov_sequences"
            : options.Tarpit.PostgresMarkov.SequencesTableName.Trim();
        options.Tarpit.PostgresMarkov.RefreshMinutes = Math.Max(1, options.Tarpit.PostgresMarkov.RefreshMinutes);

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

        options.Escalation.ConfiguredRanges.Entries = options.Escalation.ConfiguredRanges.Entries
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Cidr))
            .Select(entry => new ReputationRangeEntry
            {
                Name = string.IsNullOrWhiteSpace(entry.Name) ? entry.Cidr.Trim() : entry.Name.Trim(),
                Cidr = entry.Cidr.Trim(),
                ScoreAdjustment = entry.ScoreAdjustment,
                Signals = entry.Signals
                    .Where(signal => !string.IsNullOrWhiteSpace(signal))
                    .Select(signal => signal.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            })
            .ToArray();

        options.Escalation.HttpReputation.Endpoint = options.Escalation.HttpReputation.Endpoint.Trim();
        options.Escalation.HttpReputation.ApiKeyHeaderName =
            string.IsNullOrWhiteSpace(options.Escalation.HttpReputation.ApiKeyHeaderName)
                ? "X-Api-Key"
                : options.Escalation.HttpReputation.ApiKeyHeaderName.Trim();
        options.Escalation.HttpReputation.ApiKey = options.Escalation.HttpReputation.ApiKey.Trim();
        options.Escalation.HttpReputation.TimeoutSeconds = Math.Max(1, options.Escalation.HttpReputation.TimeoutSeconds);

        options.Escalation.OpenAiCompatibleModel.Endpoint = options.Escalation.OpenAiCompatibleModel.Endpoint.Trim();
        options.Escalation.OpenAiCompatibleModel.ApiKey = options.Escalation.OpenAiCompatibleModel.ApiKey.Trim();
        options.Escalation.OpenAiCompatibleModel.Model = options.Escalation.OpenAiCompatibleModel.Model.Trim();
        options.Escalation.OpenAiCompatibleModel.SystemPrompt =
            string.IsNullOrWhiteSpace(options.Escalation.OpenAiCompatibleModel.SystemPrompt)
                ? "You are classifying incoming web requests for scraping-defense enforcement. Return JSON with classification and summary."
                : options.Escalation.OpenAiCompatibleModel.SystemPrompt.Trim();
        options.Escalation.OpenAiCompatibleModel.TimeoutSeconds = Math.Max(1, options.Escalation.OpenAiCompatibleModel.TimeoutSeconds);

        options.CommunityBlocklist.SyncIntervalMinutes = Math.Max(1, options.CommunityBlocklist.SyncIntervalMinutes);
        options.CommunityBlocklist.RequestTimeoutSeconds = Math.Max(1, options.CommunityBlocklist.RequestTimeoutSeconds);
        options.CommunityBlocklist.MaximumEntriesPerSource = Math.Max(1, options.CommunityBlocklist.MaximumEntriesPerSource);
        options.CommunityBlocklist.Sources = options.CommunityBlocklist.Sources
            .Where(source => !string.IsNullOrWhiteSpace(source.Url))
            .Select(source => new CommunityBlocklistSourceOptions
            {
                Name = string.IsNullOrWhiteSpace(source.Name) ? source.Url.Trim() : source.Name.Trim(),
                Url = source.Url.Trim(),
                ApiKeyHeaderName = string.IsNullOrWhiteSpace(source.ApiKeyHeaderName)
                    ? "X-API-Key"
                    : source.ApiKeyHeaderName.Trim(),
                ApiKey = source.ApiKey.Trim()
            })
            .ToArray();

        options.PeerSync.SyncIntervalMinutes = Math.Max(1, options.PeerSync.SyncIntervalMinutes);
        options.PeerSync.RequestTimeoutSeconds = Math.Max(1, options.PeerSync.RequestTimeoutSeconds);
        options.PeerSync.MaximumSignalsPerPeer = Math.Max(1, options.PeerSync.MaximumSignalsPerPeer);
        options.PeerSync.MaximumExportSignals = Math.Max(1, options.PeerSync.MaximumExportSignals);
        options.PeerSync.ExportApiKeyHeaderName = string.IsNullOrWhiteSpace(options.PeerSync.ExportApiKeyHeaderName)
            ? "X-Peer-Key"
            : options.PeerSync.ExportApiKeyHeaderName.Trim();
        options.PeerSync.ExportApiKey = options.PeerSync.ExportApiKey.Trim();
        options.PeerSync.Peers = options.PeerSync.Peers
            .Where(peer => !string.IsNullOrWhiteSpace(peer.Url))
            .Select(peer => new PeerSyncPeerOptions
            {
                Name = string.IsNullOrWhiteSpace(peer.Name) ? peer.Url.Trim() : peer.Name.Trim(),
                Url = peer.Url.Trim(),
                ApiKeyHeaderName = string.IsNullOrWhiteSpace(peer.ApiKeyHeaderName)
                    ? "X-Peer-Key"
                    : peer.ApiKeyHeaderName.Trim(),
                ApiKey = peer.ApiKey.Trim(),
                TrustMode = string.Equals(peer.TrustMode, PeerTrustModes.BlockList, StringComparison.OrdinalIgnoreCase)
                    ? PeerTrustModes.BlockList
                    : PeerTrustModes.ObserveOnly
            })
            .ToArray();

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
builder.Services.AddSingleton<ITarpitMarkovStore, PostgresTarpitMarkovStore>();
builder.Services.AddSingleton<ITarpitPageService, TarpitPageService>();
builder.Services.AddSingleton<IClientIpResolver, ClientIpResolver>();
builder.Services.AddSingleton<IThreatReputationProvider, ConfiguredRangeReputationProvider>();
builder.Services.AddSingleton<IThreatReputationProvider, HttpReputationProvider>();
builder.Services.AddSingleton<IThreatModelAdapter, OpenAiCompatibleModelAdapter>();
builder.Services.AddSingleton<IThreatAssessmentService, ThreatAssessmentService>();
builder.Services.AddSingleton<ICommunityBlocklistFeedClient, HttpCommunityBlocklistFeedClient>();
builder.Services.AddSingleton<ICommunityBlocklistSyncStatusStore, CommunityBlocklistSyncStatusStore>();
builder.Services.AddSingleton<CommunityBlocklistSyncRunner>();
builder.Services.AddSingleton<IPeerSignalFeedClient, HttpPeerSignalFeedClient>();
builder.Services.AddSingleton<IPeerSyncStatusStore, PeerSyncStatusStore>();
builder.Services.AddSingleton<PeerSyncRunner>();
builder.Services.AddSingleton<ApiKeyEndpointFilter>();
builder.Services.AddSingleton<IntakeApiKeyEndpointFilter>();
builder.Services.AddSingleton<PeerApiKeyEndpointFilter>();
builder.Services.AddSingleton<IWebhookEventInbox, SqliteWebhookEventInbox>();
builder.Services.AddHostedService<StartupValidationService>();
builder.Services.AddHostedService<DefenseAnalysisService>();
builder.Services.AddHostedService<WebhookIntakeProcessingService>();
builder.Services.AddHostedService<CommunityBlocklistSyncService>();
builder.Services.AddHostedService<PeerSyncService>();

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
    mode = "commercial_v1",
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
Program.MapPeerSyncEndpoints(app, runtimeOptions);

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

        if (ShouldExposePeerSyncEndpoints(runtimeOptions))
        {
            endpoints["peerSignals"] = "/peer-sync/signals";
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

        management.MapGet("/community-blocklist/status", (
            [FromServices] ICommunityBlocklistSyncStatusStore statusStore) =>
        {
            return Results.Ok(statusStore.GetStatus());
        });

        management.MapGet("/peer-sync/status", (
            [FromServices] IPeerSyncStatusStore statusStore) =>
        {
            return Results.Ok(statusStore.GetStatus());
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

    public static void MapPeerSyncEndpoints(
        IEndpointRouteBuilder app,
        DefenseEngineOptions runtimeOptions)
    {
        if (!ShouldExposePeerSyncEndpoints(runtimeOptions))
        {
            return;
        }

        app.MapGet("/peer-sync/signals", (
            [FromQuery] int count,
            IDefenseEventStore store,
            IOptions<DefenseEngineOptions> options) =>
        {
            return Results.Ok(GetPeerSignalsForExport(store, options.Value, count));
        })
        .AddEndpointFilter<PeerApiKeyEndpointFilter>();
    }

    public static PeerDefenseSignalEnvelope GetPeerSignalsForExport(
        IDefenseEventStore store,
        DefenseEngineOptions runtimeOptions,
        int count)
    {
        var maxSignals = runtimeOptions.PeerSync.MaximumExportSignals;
        var safeCount = Math.Clamp(count <= 0 ? maxSignals : count, 1, maxSignals);
        var scanWindow = Math.Max(safeCount, runtimeOptions.Audit.MaxRecentEvents);
        var signals = store.GetRecent(scanWindow)
            .Where(decision => string.Equals(decision.Action, "blocked", StringComparison.OrdinalIgnoreCase))
            .Take(safeCount)
            .Select(decision => new PeerDefenseSignal(
                decision.IpAddress,
                decision.Summary,
                decision.Signals,
                decision.ObservedAtUtc,
                decision.DecidedAtUtc))
            .ToArray();

        return new PeerDefenseSignalEnvelope(
            "ai-scraping-defense-dotnet",
            signals);
    }

    public static string GetTarpitRoutePattern(DefenseEngineOptions runtimeOptions)
    {
        return $"{runtimeOptions.Tarpit.PathPrefix}/{{**path}}";
    }

    public static bool ShouldExposeIntakeEndpoints(DefenseEngineOptions runtimeOptions)
    {
        return !string.IsNullOrWhiteSpace(runtimeOptions.Intake.ApiKey);
    }

    public static bool ShouldExposePeerSyncEndpoints(DefenseEngineOptions runtimeOptions)
    {
        return !string.IsNullOrWhiteSpace(runtimeOptions.PeerSync.ExportApiKey);
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
