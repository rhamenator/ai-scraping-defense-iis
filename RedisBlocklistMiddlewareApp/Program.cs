using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;
using RedisBlocklistMiddlewareApp.Services.LinuxEngineClient;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<DefenseEngineOptions>(
    builder.Configuration.GetSection(DefenseEngineOptions.SectionName));

builder.Services.AddHttpClient<IDefenseEngineClient, LinuxDefenseEngineClient>();
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
    return Results.Ok(status);
});

app.MapGet("/api/control/telemetry", async (ITelemetryService telemetryService, CancellationToken ct) =>
{
    var telemetry = await telemetryService.GetCachedTelemetryAsync(ct)
                    ?? await telemetryService.RefreshTelemetryAsync(ct);
    return Results.Ok(telemetry);
});

app.MapPost("/api/control/policies", async (PolicySubmissionRequest request, IPolicyService policyService, CancellationToken ct) =>
{
    var response = await policyService.PushPolicyAsync(request, ct);
    return Results.Ok(response);
});

app.MapPost("/api/control/escalations/ack", async (EscalationAcknowledgementRequest request, IDefenseEngineClient engineClient, CancellationToken ct) =>
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
