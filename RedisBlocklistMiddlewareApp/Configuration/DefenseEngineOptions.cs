using System.ComponentModel.DataAnnotations;

namespace RedisBlocklistMiddlewareApp.Configuration;

public class DefenseEngineOptions
{
    public const string SectionName = "DefenseEngine";

    [Required(AllowEmptyStrings = false)]
    public string EngineEndpoint { get; set; } = "http://localhost:8080";
    public string? ApiKey { get; set; }
    public string? BearerToken { get; set; }
    /// <summary>Inbound API key required on X-Control-API-Key header for /api/control/* endpoints.</summary>
    public string? ControlApiKey { get; set; }
    [Range(1, int.MaxValue, ErrorMessage = "TimeoutSeconds must be at least 1.")]
    public int TimeoutSeconds { get; set; } = 10;
    public RetryPolicyOptions RetryPolicy { get; set; } = new();
}

public class RetryPolicyOptions
{
    [Range(1, int.MaxValue, ErrorMessage = "MaxAttempts must be at least 1.")]
    public int MaxAttempts { get; set; } = 3;
    [Range(1, int.MaxValue, ErrorMessage = "BaseDelayMilliseconds must be at least 1.")]
    public int BaseDelayMilliseconds { get; set; } = 250;
}
