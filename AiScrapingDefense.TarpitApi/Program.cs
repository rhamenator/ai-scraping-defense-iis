using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services
    .AddOptions<DefenseEngineOptions>()
    .Bind(builder.Configuration.GetSection(DefenseEngineOptions.SectionName));
builder.Services.AddSingleton<ITarpitMarkovStore, PostgresTarpitMarkovStore>();
builder.Services.AddSingleton<ITarpitPageService, TarpitPageService>();
builder.Services.AddSingleton<ITarpitArtifactService, TarpitArtifactService>();
builder.Services.AddSingleton(TimeProvider.System);

var app = builder.Build();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", runtime = "tarpit-api" }));
app.MapGet("/live", () => Results.Ok(new { status = "alive", runtime = "tarpit-api" }));
app.MapGet("/tarpit/{**path}", async (
    HttpContext context,
    string? path,
    ITarpitPageService pageService,
    ITarpitArtifactService artifactService,
    IOptions<DefenseEngineOptions> options,
    CancellationToken cancellationToken) =>
{
    var delay = Math.Max(0, options.Value.Tarpit.ResponseDelayMilliseconds);
    if (delay > 0)
    {
        await Task.Delay(delay, cancellationToken);
    }

    var normalizedPath = path ?? string.Empty;
    var artifact = await artifactService.TryGetArtifactAsync(normalizedPath, cancellationToken);
    if (artifact is not null)
    {
        return Results.File(artifact.Content, artifact.ContentType, artifact.FileName);
    }

    var clientIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    return Results.Content(pageService.GeneratePage(normalizedPath, clientIp), "text/html");
});
app.Run();
