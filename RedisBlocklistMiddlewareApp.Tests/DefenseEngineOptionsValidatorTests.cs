using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class DefenseEngineOptionsValidatorTests
{
    [Fact]
    public void Validate_AcceptsConfiguredSplitTopology()
    {
        var validator = new DefenseEngineOptionsValidator();
        var result = validator.Validate(
            null,
            new DefenseEngineOptions
            {
                Topology = new TopologyOptions
                {
                    Mode = RuntimeTopologyModes.Split,
                    EscalationBaseUrl = "http://escalation:8080",
                    TarpitPublicBaseUrl = "https://tarpit.example.test",
                    ServiceToken = new string('s', 32)
                }
            });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_RejectsIncompleteSplitTopology()
    {
        var validator = new DefenseEngineOptionsValidator();
        var result = validator.Validate(
            null,
            new DefenseEngineOptions
            {
                Topology = new TopologyOptions { Mode = RuntimeTopologyModes.Split }
            });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("EscalationBaseUrl", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("ServiceToken", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AcceptsSingleTopology()
    {
        var validator = new DefenseEngineOptionsValidator();
        var result = validator.Validate(null, new DefenseEngineOptions());

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_AcceptsSingleNodeDurableConsensusForDevelopment()
    {
        var validator = new DefenseEngineOptionsValidator();
        var result = validator.Validate(
            null,
            new DefenseEngineOptions
            {
                Consensus = new ConsensusOptions
                {
                    Enabled = true,
                    ListenAddress = "0.0.0.0",
                    AdvertisedHost = "edge-0",
                    Port = 3262,
                    StoragePath = "data/raft",
                    SharedSecret = new string('c', 32),
                    Members =
                    [
                        new ConsensusMemberOptions
                        {
                            RaftEndpoint = "edge-0:3262",
                            ApiBaseUrl = "http://edge-0:8080"
                        }
                    ]
                }
            });

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Validate_RejectsConsensusWithoutSafeQuorumOrLocalMembership()
    {
        var validator = new DefenseEngineOptionsValidator();
        var result = validator.Validate(
            null,
            new DefenseEngineOptions
            {
                Consensus = new ConsensusOptions
                {
                    Enabled = true,
                    ListenAddress = "0.0.0.0",
                    AdvertisedHost = "edge-0",
                    Port = 3262,
                    StoragePath = "data/raft",
                    SharedSecret = "short",
                    Members =
                    [
                        new ConsensusMemberOptions { RaftEndpoint = "edge-1:3262", ApiBaseUrl = "http://edge-1:8080" },
                        new ConsensusMemberOptions { RaftEndpoint = "edge-2:3262", ApiBaseUrl = "http://edge-2:8080" }
                    ]
                }
            });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("odd production quorum", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("SharedSecret", StringComparison.Ordinal));
        Assert.Contains(result.Failures, failure => failure.Contains("edge-0:3262", StringComparison.Ordinal));
    }

    [Fact]
    public void IsBearerTokenValid_UsesExactBearerToken()
    {
        var token = new string('t', 32);

        Assert.True(Program.IsBearerTokenValid($"Bearer {token}", token));
        Assert.False(Program.IsBearerTokenValid($"Bearer {token}x", token));
        Assert.False(Program.IsBearerTokenValid(token, token));
    }

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

    [Fact]
    public void Validate_RejectsUnknownAuditProvider()
    {
        var validator = new DefenseEngineOptionsValidator();
        var result = validator.Validate(
            null,
            new DefenseEngineOptions
            {
                Audit = new AuditOptions
                {
                    Provider = "Oracle"
                }
            });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("Audit:Provider", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RequiresConnectionStringForRelationalAuditProvider()
    {
        var validator = new DefenseEngineOptionsValidator();
        var result = validator.Validate(
            null,
            new DefenseEngineOptions
            {
                Audit = new AuditOptions
                {
                    Provider = AuditStorageProviders.Postgres
                }
            });

        Assert.True(result.Failed);
        Assert.Contains(result.Failures, failure => failure.Contains("Audit:ConnectionString", StringComparison.Ordinal));
    }
}
