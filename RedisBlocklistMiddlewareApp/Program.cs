using System.Net.Http.Headers;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;
using RedisBlocklistMiddlewareApp.Services.LinuxEngineClient;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOptions<DefenseEngineOptions>()
    .Bind(builder.Configuration.GetSection(DefenseEngineOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<DefenseEngineApiRoutesOptions>()
    .Bind(builder.Configuration.GetSection(DefenseEngineApiRoutesOptions.SectionName));

builder.Services.AddOptions<DefenseEngineSyncOptions>()
    .Bind(builder.Configuration.GetSection(DefenseEngineSyncOptions.SectionName));

builder.Services.AddHttpClient(LinuxDefenseEngineClient.HttpClientName, (sp, client) =>
{
    var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<DefenseEngineOptions>>().Value;
    client.BaseAddress = new Uri(options.EngineEndpoint);
    client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds));
    if (!string.IsNullOrWhiteSpace(options.BearerToken))
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.BearerToken);
    if (!string.IsNullOrWhiteSpace(options.ApiKey))
        client.DefaultRequestHeaders.Add("X-API-Key", options.ApiKey);
});
builder.Services.AddSingleton<IDefenseEngineClient, LinuxDefenseEngineClient>();
builder.Services.AddSingleton<ITelemetryService, TelemetryService>();
builder.Services.AddSingleton<IPolicyService, PolicyService>();
builder.Services.AddHostedService<DefenseEngineSyncService>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/health", async (IDefenseEngineClient engineClient, CancellationToken ct) =>
{
    var status = await engineClient.GetHealthAsync(ct);
    return string.Equals(status.Status, "unreachable", StringComparison.OrdinalIgnoreCase)
        ? Results.Json(status, statusCode: 503)
        : Results.Ok(status);
});

var control = app.MapGroup("/api/control")
    .AddEndpointFilter<ApiKeyEndpointFilter>();

control.MapGet("/telemetry", async (ITelemetryService telemetryService, CancellationToken ct) =>
{
    var telemetry = await telemetryService.GetCachedTelemetryAsync(ct)
                    ?? await telemetryService.RefreshTelemetryAsync(ct);
    return Results.Ok(telemetry);
});

control.MapPost("/policies", async (PolicySubmissionRequest request, IPolicyService policyService, CancellationToken ct) =>
{
    var response = await policyService.PushPolicyAsync(request, ct);
    return Results.Ok(response);
});

control.MapPost("/escalations/ack", async (EscalationAcknowledgementRequest request, IDefenseEngineClient engineClient, CancellationToken ct) =>
{
    var response = await engineClient.AcknowledgeEscalationAsync(request, ct);
    return Results.Ok(response);
});

app.MapGet("/", () => Results.Ok(new
{
    service = "ai-scraping-defense-iis control plane",
    mode = "adapter",
    linuxEngine = "remote"
}));

app.Run();
