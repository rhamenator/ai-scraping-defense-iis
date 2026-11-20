namespace RedisBlocklistMiddlewareApp.Services;

public interface IRobotsPolicyProvider
{
    /// <summary>The parsed policy. Empty when robots enforcement is disabled.</summary>
    RobotsPolicy Policy { get; }

    /// <summary>The robots.txt text served to crawlers, or null when serving is disabled.</summary>
    string? ServedText { get; }
}
