using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class ThreatModelRoutingStrategyTests
{
    [Fact]
    public void BuildPlan_UsesAvailableFallbackRouteWhenPreferredRouteHasNoAdapters()
    {
        var strategy = CreateStrategy(options =>
        {
            options.Escalation.Routing.PreferredPrimaryRoute = ThreatModelRoutes.Local;
            options.Escalation.Routing.RemoteFallbackEnabled = true;
        });

        var remoteAdapter = new TestModelAdapter(ThreatModelRoutes.Remote);
        var plan = strategy.BuildPlan(CreateContext(), [remoteAdapter]);

        Assert.Equal(ThreatModelRoutes.Remote, plan.PrimaryRoute);
        Assert.False(plan.FallbackEnabled);
        Assert.Same(remoteAdapter, Assert.Single(plan.OrderedAdapters));
    }

    [Fact]
    public void BuildPlan_DoesNotAdvertiseFallbackWhenNoFallbackAdaptersExist()
    {
        var strategy = CreateStrategy(options =>
        {
            options.Escalation.Routing.PreferredPrimaryRoute = ThreatModelRoutes.Local;
            options.Escalation.Routing.RemoteFallbackEnabled = true;
        });

        var localAdapter = new TestModelAdapter(ThreatModelRoutes.Local);
        var plan = strategy.BuildPlan(CreateContext(), [localAdapter]);

        Assert.Equal(ThreatModelRoutes.Local, plan.PrimaryRoute);
        Assert.False(plan.FallbackEnabled);
        Assert.Same(localAdapter, Assert.Single(plan.OrderedAdapters));
    }

    private static ThreatModelRoutingStrategy CreateStrategy(Action<DefenseEngineOptions>? configure = null)
    {
        var options = new DefenseEngineOptions();
        configure?.Invoke(options);
        return new ThreatModelRoutingStrategy(Options.Create(options));
    }

    private static ThreatAssessmentContext CreateContext()
    {
        return new ThreatAssessmentContext(
            "198.51.100.50",
            "GET",
            "/probe",
            string.Empty,
            "test-agent",
            [],
            1,
            0,
            0);
    }

    private sealed class TestModelAdapter : IThreatModelAdapter
    {
        public TestModelAdapter(string route)
        {
            Route = route;
        }

        public string Name => "test_model";

        public string Route { get; }

        public Task<ModelAssessment?> AssessAsync(ThreatAssessmentContext context, CancellationToken cancellationToken)
        {
            return Task.FromResult<ModelAssessment?>(null);
        }
    }
}