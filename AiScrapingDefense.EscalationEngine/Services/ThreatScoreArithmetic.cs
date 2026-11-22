namespace RedisBlocklistMiddlewareApp.Services;

/// <summary>
/// Saturating arithmetic for threat scores (#202). Contributions come from configuration and from remote services; a plain
/// <c>int</c> sum wraps around on overflow, so an enormous positive adjustment could become negative and be floored to zero,
/// erasing the other evidence.
/// </summary>
public static class ThreatScoreArithmetic
{
    /// <summary>No running score is ever allowed outside ±this value, far beyond any block threshold.</summary>
    public const int MaximumMagnitude = 1_000_000;

    public static int Add(int total, int delta) =>
        (int)Math.Clamp((long)total + delta, -MaximumMagnitude, MaximumMagnitude);
}
