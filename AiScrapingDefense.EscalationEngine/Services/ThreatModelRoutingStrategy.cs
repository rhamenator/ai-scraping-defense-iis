using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class ThreatModelRoutingStrategy : IThreatModelRoutingStrategy
{
    private readonly ThreatModelRoutingOptions _options;

    public ThreatModelRoutingStrategy(IOptions<DefenseEngineOptions> options)
    {
        _options = options.Value.Escalation.Routing;
    }

    public ThreatModelRoutingPlan BuildPlan(
        ThreatAssessmentContext context,
        IReadOnlyList<IThreatModelAdapter> adapters)
    {
        if (!_options.Enabled || adapters.Count == 0)
        {
            return new ThreatModelRoutingPlan(ThreatModelRoutes.Any, false, adapters);
        }

        var primaryRoute = DeterminePrimaryRoute(context);
        var fallbackEnabled = string.Equals(primaryRoute, ThreatModelRoutes.Local, StringComparison.OrdinalIgnoreCase)
            ? _options.RemoteFallbackEnabled
            : _options.LocalFallbackEnabled;

        var primaryAdapters = adapters
            .Where(adapter => MatchesRoute(adapter.Route, primaryRoute) || MatchesRoute(adapter.Route, ThreatModelRoutes.Any))
            .ToArray();
        var fallbackRoute = string.Equals(primaryRoute, ThreatModelRoutes.Local, StringComparison.OrdinalIgnoreCase)
            ? ThreatModelRoutes.Remote
            : ThreatModelRoutes.Local;
        var fallbackAdapters = fallbackEnabled
            ? adapters
                .Where(adapter => !primaryAdapters.Contains(adapter) && MatchesRoute(adapter.Route, fallbackRoute))
                .ToArray()
            : [];

        return new ThreatModelRoutingPlan(
            primaryRoute,
            fallbackEnabled,
            primaryAdapters.Concat(fallbackAdapters).ToArray());
    }

    private string DeterminePrimaryRoute(ThreatAssessmentContext context)
    {
        if (string.Equals(_options.PreferredPrimaryRoute, ThreatModelRoutes.Local, StringComparison.OrdinalIgnoreCase))
        {
            return ThreatModelRoutes.Local;
        }

        if (string.Equals(_options.PreferredPrimaryRoute, ThreatModelRoutes.Remote, StringComparison.OrdinalIgnoreCase))
        {
            return ThreatModelRoutes.Remote;
        }

        var queryLength = context.QueryString?.Length ?? 0;
        var signalCount = context.Signals.Count;

        return signalCount <= _options.MaxSignalsForLocalRoute &&
            queryLength <= _options.MaxQueryStringLengthForLocalRoute &&
            context.Frequency <= _options.MaxFrequencyForLocalRoute
            ? ThreatModelRoutes.Local
            : ThreatModelRoutes.Remote;
    }

    private static bool MatchesRoute(string actual, string expected)
    {
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }
}