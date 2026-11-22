using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddOptions<DefenseEngineOptions>()
    .Bind(builder.Configuration.GetSection(DefenseEngineOptions.SectionName))
    .Validate(options => options.Tarpit.FlaggedIpTtlSeconds > 0 &&
                         options.Tarpit.RepeatOffenderThreshold >= 0 &&
                         options.Tarpit.MaxHops >= 0 && options.Tarpit.HopWindowSeconds > 0,
        "Tarpit hop and flag settings must be valid.")
    .Validate(options =>
    {
        var networking = options.Networking;
        if (!string.Equals(networking.ClientIpResolutionMode, ClientIpResolutionModes.TrustedProxy,
                StringComparison.OrdinalIgnoreCase)) return true;
        var trusted = networking.TrustedProxies.Concat(networking.TrustedCdnProxies).ToArray();
        return trusted.Length > 0 && trusted.All(entry =>
            IPAddress.TryParse(entry, out _) || System.Net.IPNetwork.TryParse(entry, out _));
    }, "TrustedProxy mode requires valid trusted proxy addresses.")
    .ValidateOnStart();
builder.Services.AddSingleton<ITarpitMarkovStore, PostgresTarpitMarkovStore>();
builder.Services.AddSingleton<ITarpitPageService, TarpitPageService>();
builder.Services.AddSingleton<ITarpitArtifactService, TarpitArtifactService>();
builder.Services.AddSingleton(sp => new TarpitHopLimiter(
    sp.GetRequiredService<IOptions<DefenseEngineOptions>>().Value,
    sp.GetRequiredService<ILogger<TarpitHopLimiter>>()));
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.None;
    options.ForwardLimit = 1;
    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();
    if (!string.Equals(
        builder.Configuration["DefenseEngine:Networking:ClientIpResolutionMode"],
        ClientIpResolutionModes.TrustedProxy,
        StringComparison.OrdinalIgnoreCase)) return;
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor;
    var trusted = builder.Configuration.GetSection("DefenseEngine:Networking:TrustedProxies").Get<string[]>() ?? [];
    var cdn = builder.Configuration.GetSection("DefenseEngine:Networking:TrustedCdnProxies").Get<string[]>() ?? [];
    foreach (var entry in trusted.Concat(cdn).Distinct(StringComparer.OrdinalIgnoreCase))
    {
        if (IPAddress.TryParse(entry, out var address)) options.KnownProxies.Add(address);
        else
        {
            var parts = entry.Split('/', StringSplitOptions.TrimEntries);
            if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var network) &&
                int.TryParse(parts[1], out var prefix))
                options.KnownIPNetworks.Add(new System.Net.IPNetwork(network, prefix));
        }
    }
});

var app = builder.Build();
app.UseForwardedHeaders();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", runtime = "tarpit-api" }));
app.MapGet("/live", () => Results.Ok(new { status = "alive", runtime = "tarpit-api" }));
app.MapGet("/tarpit/{**path}", async (
    HttpContext context,
    string? path,
    ITarpitPageService pageService,
    ITarpitArtifactService artifactService,
    TarpitHopLimiter hopLimiter,
    ILogger<TarpitApiAssemblyMarker> logger,
    IOptions<DefenseEngineOptions> options,
    CancellationToken cancellationToken) =>
{
    // Filler content must never be indexed under the protected site's domain.
    context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    var clientIp = ResolveTarpitClientIp(context, options.Value.Networking) ?? "unknown";
    var count = await hopLimiter.CountFlaggedHopAsync(clientIp);
    if (count > options.Value.Tarpit.MaxHops)
    {
        var reason = $"Tarpit hop limit exceeded ({count} hits in {options.Value.Tarpit.HopWindowSeconds}s)";
        await hopLimiter.BlockAsync(clientIp, reason);
        return Results.Content("<html><head><title>Forbidden</title></head><body>Access Denied.</body></html>", "text/html", statusCode: 403);
    }
    var delay = Math.Max(0, options.Value.Tarpit.ResponseDelayMilliseconds);
    if (delay > 0)
    {
        await Task.Delay(delay, cancellationToken);
    }

    var normalizedPath = path ?? string.Empty;
    logger.LogInformation("TAR_PIT HIT: Path={Path}, IP={IpAddress}, Reason={Reason}",
        context.Request.Path, clientIp, "Tarpit Hit");
    var artifact = await artifactService.TryGetArtifactAsync(normalizedPath, cancellationToken);
    if (artifact is not null)
    {
        await hopLimiter.FlagAsync(clientIp);
        return Results.File(artifact.Content, artifact.ContentType, artifact.FileName);
    }

    await hopLimiter.FlagAsync(clientIp);
    return Results.Content(pageService.GeneratePage(normalizedPath, clientIp), "text/html");
});
app.Run();

static string? ResolveTarpitClientIp(HttpContext context, NetworkingOptions networking)
{
    var address = context.Connection.RemoteIpAddress;
    if (address is null) return null;
    if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
    if (string.Equals(networking.ClientIpResolutionMode, ClientIpResolutionModes.TrustedProxy,
            StringComparison.OrdinalIgnoreCase) &&
        networking.TrustedProxies.Concat(networking.TrustedCdnProxies).Any(entry =>
            IPAddress.TryParse(entry, out var proxy) && proxy.Equals(address) ||
            System.Net.IPNetwork.TryParse(entry, out var network) && network.Contains(address)))
        return null;
    return address.ToString();
}

public sealed class TarpitApiAssemblyMarker;

internal partial class Program;
