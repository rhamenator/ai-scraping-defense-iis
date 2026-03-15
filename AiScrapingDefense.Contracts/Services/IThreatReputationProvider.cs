namespace RedisBlocklistMiddlewareApp.Services;

public interface IThreatReputationProvider
{
    string Name { get; }

    Task<ReputationAssessment?> AssessAsync(
        ThreatAssessmentContext context,
        CancellationToken cancellationToken);
}
