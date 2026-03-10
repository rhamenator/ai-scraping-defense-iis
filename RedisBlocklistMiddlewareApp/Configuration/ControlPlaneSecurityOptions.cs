namespace RedisBlocklistMiddlewareApp.Configuration;

public class ControlPlaneSecurityOptions
{
    public const string SectionName = "ControlPlaneSecurity";

    /// <summary>
    /// API key required in the X-Control-Plane-Key header for state-mutating endpoints.
    /// When empty or null, key validation is skipped (development convenience only).
    /// </summary>
    public string? ApiKey { get; set; }
}
