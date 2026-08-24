using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;
using RedisBlocklistMiddlewareApp.Security;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 256 * 1024;
});
builder.Services
    .AddOptions<DefenseEngineOptions>()
    .Bind(builder.Configuration.GetSection(DefenseEngineOptions.SectionName))
    .PostConfigure(options => McpModelEnvironmentConfiguration.Apply(options));
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IConnectionMultiplexer>(services =>
{
    var options = services.GetRequiredService<IOptions<DefenseEngineOptions>>().Value.Redis;
    var configuration = ConfigurationOptions.Parse(options.ConnectionString);
    configuration.AbortOnConnectFail = false;
    return ConnectionMultiplexer.Connect(configuration);
});
builder.Services.AddSingleton<IRequestFrequencyTracker, EscalationRedisFrequencyTracker>();
builder.Services.AddSingleton<IThreatScoreContributor, EdgeSignalScoreContributor>();
builder.Services.AddSingleton<IThreatScoreContributor, FrequencyScoreContributor>();
builder.Services.AddSingleton<IThreatReputationProvider, ConfiguredRangeReputationProvider>();
builder.Services.AddSingleton<IThreatReputationProvider, HttpReputationProvider>();
builder.Services.AddSingleton<IThreatModelAdapter, LocalTrainedModelAdapter>();
builder.Services.AddSingleton<IThreatModelAdapter, OpenAiCompatibleModelAdapter>();
builder.Services.AddSingleton<IThreatModelAdapter, McpModelAdapter>();
builder.Services.AddSingleton<IThreatModelRoutingStrategy, ThreatModelRoutingStrategy>();
builder.Services.AddSingleton<IContainmentDecisionContributor, ExplicitVerdictContainmentContributor>();
builder.Services.AddSingleton<IContainmentDecisionContributor, FrequencyContainmentContributor>();
builder.Services.AddSingleton<IContainmentDecisionContributor, ThresholdBandContainmentContributor>();
builder.Services.AddSingleton<IContainmentPolicyEngine, ContainmentPolicyEngine>();
builder.Services.AddSingleton<IAssessmentTelemetry, EscalationAssessmentTelemetry>();
builder.Services.AddSingleton<IThreatAssessmentService, ThreatAssessmentService>();

var app = builder.Build();
var runtimeOptions = app.Services.GetRequiredService<IOptions<DefenseEngineOptions>>().Value;
if (string.IsNullOrWhiteSpace(runtimeOptions.Topology.ServiceToken) ||
    runtimeOptions.Topology.ServiceToken.Length < 32)
{
    throw new InvalidOperationException(
        "DefenseEngine:Topology:ServiceToken must contain at least 32 characters.");
}
if ((!string.IsNullOrEmpty(runtimeOptions.TlsFingerprints.AttestationKey) &&
     runtimeOptions.TlsFingerprints.AttestationKey.Length < 32) ||
    (!string.IsNullOrEmpty(runtimeOptions.TlsFingerprints.PreviousAttestationKey) &&
     runtimeOptions.TlsFingerprints.PreviousAttestationKey.Length < 32) ||
    runtimeOptions.TlsFingerprints.AttestationMaxAgeSeconds <= 0)
{
    throw new InvalidOperationException(
        "TLS fingerprint attestation requires a key of at least 32 characters and a positive max age.");
}

app.MapGet("/health", async (IConnectionMultiplexer redis) =>
{
    try
    {
        await redis.GetDatabase(runtimeOptions.Redis.FrequencyDatabase).PingAsync();
        return Results.Ok(new { status = "healthy", runtime = "escalation-engine" });
    }
    catch
    {
        return Results.Json(
            new { status = "degraded", runtime = "escalation-engine" },
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});
app.MapGet("/live", () => Results.Ok(new { status = "alive", runtime = "escalation-engine" }));

app.MapPost("/v1/assess", async (
    HttpContext context,
    SuspiciousRequest request,
    IThreatAssessmentService assessmentService,
    CancellationToken cancellationToken) =>
{
    if (!HasValidServiceToken(context, runtimeOptions.Topology.ServiceToken))
    {
        return Results.Unauthorized();
    }
    if (!IsValidAssessmentRequest(request))
    {
        return Results.BadRequest(new { error = "The assessment request is invalid or exceeds a field limit." });
    }
    request = VerifyTlsFingerprint(request, runtimeOptions.TlsFingerprints);
    var result = await assessmentService.AssessAsync(request, cancellationToken);
    return Results.Ok(result);
});

app.Run();

static bool HasValidServiceToken(HttpContext context, string expectedToken)
{
    var authorization = context.Request.Headers.Authorization.ToString();
    const string prefix = "Bearer ";
    if (!authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }
    var supplied = authorization[prefix.Length..];
    var expectedBytes = Encoding.UTF8.GetBytes(expectedToken);
    var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
    return expectedBytes.Length == suppliedBytes.Length &&
        CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes);
}

static bool IsValidAssessmentRequest(SuspiciousRequest request)
{
    return System.Net.IPAddress.TryParse(request.IpAddress, out _) &&
        !string.IsNullOrWhiteSpace(request.Method) && request.Method.Length <= 16 &&
        request.Path is not null && request.Path.Length <= 4096 &&
        request.QueryString is not null && request.QueryString.Length <= 8192 &&
        request.UserAgent is not null && request.UserAgent.Length <= 2048 &&
        request.Signals is not null && request.Signals.Count <= 128 &&
        request.Signals.All(signal => signal is not null && signal.Length <= 512);
}

static SuspiciousRequest VerifyTlsFingerprint(
    SuspiciousRequest request,
    TlsFingerprintOptions options)
{
    var fingerprint = TlsFingerprintAttestation.Normalize(request.TlsFingerprint);
    if (fingerprint is null)
    {
        return request with { TlsFingerprint = null };
    }
    var verified = TlsFingerprintAttestation.Verify(
        fingerprint,
        request.IpAddress,
        request.Method,
        request.Path,
        options.AttestationKey,
        options.AttestationMaxAgeSeconds,
        previousKey: options.PreviousAttestationKey);
    return request with
    {
        TlsFingerprint = fingerprint with
        {
            Verified = verified,
            Attestation = null
        }
    };
}

public sealed class EscalationEngineAssemblyMarker;
