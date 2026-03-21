using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class DefenseEngineOptionsValidatorTests
{
    [Fact]
    public void Validate_RejectsUnknownPrimaryModelRoute()
    {
        var validator = new DefenseEngineOptionsValidator();
        var result = validator.Validate(
            null,
            new DefenseEngineOptions
            {
                Escalation = new EscalationOptions
                {
                    Routing = new ThreatModelRoutingOptions
                    {
                        PreferredPrimaryRoute = "Sideways"
                    }
                }
            });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("PreferredPrimaryRoute", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsOutOfOrderContainmentThresholds()
    {
        var validator = new DefenseEngineOptionsValidator();
        var result = validator.Validate(
            null,
            new DefenseEngineOptions
            {
                Escalation = new EscalationOptions
                {
                    Containment = new ContainmentPolicyOptions
                    {
                        ChallengeScoreThreshold = 50,
                        TarpitScoreThreshold = 40,
                        ThrottleScoreThreshold = 60,
                        BlockScoreThreshold = 80
                    }
                }
            });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("Challenge <= Tarpit <= Throttle <= Block", StringComparison.Ordinal));
    }
}