namespace RedisBlocklistMiddlewareApp.Configuration;

public class DefenseEngineOptions
{
    public const string SectionName = "DefenseEngine";

    public string EngineEndpoint { get; set; } = "http://localhost:8080";
    public string? ApiKey { get; set; }
    public string? BearerToken { get; set; }
    public int TimeoutSeconds { get; set; } = 10;
    public RetryPolicyOptions RetryPolicy { get; set; } = new();
}

public class RetryPolicyOptions
{
    public int MaxAttempts { get; set; } = 3;
    public int BaseDelayMilliseconds { get; set; } = 250;
}
