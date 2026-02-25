using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public interface IPolicyService
{
    Task<PolicySubmissionResponse> PushPolicyAsync(PolicySubmissionRequest request, CancellationToken cancellationToken = default);
    Task<ServiceOperationResult> SyncPendingPolicyChangesAsync(CancellationToken cancellationToken = default);
}
