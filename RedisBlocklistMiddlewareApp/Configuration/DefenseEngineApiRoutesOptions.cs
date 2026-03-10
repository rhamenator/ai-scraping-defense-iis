namespace RedisBlocklistMiddlewareApp.Configuration;

public class DefenseEngineApiRoutesOptions
{
    public const string SectionName = "DefenseEngine:ApiRoutes";

    public string HealthPath { get; set; } = "/health";
    public string TelemetryPath { get; set; } = "/api/v1/telemetry";
    public string PoliciesPath { get; set; } = "/api/v1/policies";
    public string EscalationAckPath { get; set; } = "/api/v1/escalations/ack";
}
