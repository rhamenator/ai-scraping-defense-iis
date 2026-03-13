namespace RedisBlocklistMiddlewareApp.Services;

public interface IThreatModelAdapter
{
    string Name { get; }

    Task<ModelAssessment?> AssessAsync(
        ThreatAssessmentContext context,
        CancellationToken cancellationToken);
}
