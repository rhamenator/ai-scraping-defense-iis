namespace RedisBlocklistMiddlewareApp.Services;

public interface IThreatModelAdapter
{
    string Name { get; }

    string Route { get; }

    Task<ModelAssessment?> AssessAsync(
        ThreatAssessmentContext context,
        CancellationToken cancellationToken);
}
