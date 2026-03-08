using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public interface IDefenseEngineClient
{
    Task<EngineHealthResponse> GetHealthAsync(CancellationToken cancellationToken = default);
    Task<TelemetrySnapshotResponse> GetTelemetryAsync(CancellationToken cancellationToken = default);
    Task<PolicySubmissionResponse> SubmitPolicyAsync(PolicySubmissionRequest request, CancellationToken cancellationToken = default);
    Task<EscalationAcknowledgementResponse> AcknowledgeEscalationAsync(EscalationAcknowledgementRequest request, CancellationToken cancellationToken = default);
}
