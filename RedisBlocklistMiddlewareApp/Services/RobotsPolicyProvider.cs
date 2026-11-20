using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

/// <summary>
/// Loads the robots.txt policy once at startup. A missing or unreadable file fails
/// startup instead of silently running without a policy.
/// </summary>
public sealed class RobotsPolicyProvider : IRobotsPolicyProvider
{
    public RobotsPolicyProvider(IOptions<DefenseEngineOptions> options)
    {
        var robots = options.Value.Robots;
        if (!robots.Enabled)
        {
            Policy = RobotsPolicy.Empty;
            return;
        }

        var path = Path.IsPathRooted(robots.PolicyPath)
            ? robots.PolicyPath
            : Path.Combine(AppContext.BaseDirectory, robots.PolicyPath);
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"DefenseEngine:Robots:PolicyPath '{path}' could not be read. " +
                "Provide a robots.txt file or set DefenseEngine:Robots:Enabled to false.",
                ex);
        }

        Policy = RobotsPolicy.Parse(text);
        ServedText = robots.ServePolicy ? text : null;
    }

    public RobotsPolicy Policy { get; }

    public string? ServedText { get; }
}
