namespace RedisBlocklistMiddlewareApp.Models;

public record EngineHealthResponse(string Status, DateTimeOffset CheckedAt, string EngineBaseUrl);

public record TelemetrySnapshotResponse(
    DateTimeOffset RetrievedAt,
    int EventCount,
    IReadOnlyList<TelemetryEvent> Events);

public record TelemetryEvent(
    string EventId,
    string Type,
    string Severity,
    string SourceIp,
    DateTimeOffset OccurredAt,
    string Summary);

public record PolicySubmissionRequest(string PolicyName, string JsonPayload, string UpdatedBy);

public record PolicySubmissionResponse(string PolicyId, string Status, DateTimeOffset SubmittedAt);

public record EscalationAcknowledgementRequest(string EscalationId, string AcknowledgedBy, string Notes);
public record EscalationAcknowledgementResponse(string EscalationId, string Status, DateTimeOffset AcknowledgedAt);

public record ServiceOperationResult(bool Success, string Message);
