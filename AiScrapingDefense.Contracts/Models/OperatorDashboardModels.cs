namespace RedisBlocklistMiddlewareApp.Models;

public sealed record DashboardSessionLoginRequest(string ApiKey);

public sealed record DefenseDecisionFeedbackCreateRequest(
    long DecisionId,
    string UpdatedAction,
    string Reason,
    string? Actor);
