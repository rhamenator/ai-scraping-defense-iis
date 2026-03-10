using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public class PolicyService : IPolicyService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PolicyService> _logger;
    private readonly ConcurrentQueue<PolicySubmissionRequest> _pendingQueue = new();

    public PolicyService(IServiceScopeFactory scopeFactory, ILogger<PolicyService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public async Task<PolicySubmissionResponse> PushPolicyAsync(PolicySubmissionRequest request, CancellationToken cancellationToken = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var client = scope.ServiceProvider.GetRequiredService<IDefenseEngineClient>();
        var response = await client.SubmitPolicyAsync(request, cancellationToken);
        if (response.Status.Equals("queued-local", StringComparison.OrdinalIgnoreCase))
        {
            _pendingQueue.Enqueue(request);
            _logger.LogWarning("Policy {PolicyName} queued locally because Linux engine is unavailable.", request.PolicyName);
        }

        return response;
    }

    public async Task<ServiceOperationResult> SyncPendingPolicyChangesAsync(CancellationToken cancellationToken = default)
    {
        if (_pendingQueue.IsEmpty)
        {
            return new ServiceOperationResult(true, "No pending policy updates.");
        }

        var synced = 0;
        var failed = 0;
        var retryBuffer = new List<PolicySubmissionRequest>();

        while (_pendingQueue.TryDequeue(out var request))
        {
            using var itemScope = _scopeFactory.CreateScope();
            var itemClient = itemScope.ServiceProvider.GetRequiredService<IDefenseEngineClient>();
            var result = await itemClient.SubmitPolicyAsync(request, cancellationToken);
            if (result.Status.Equals("queued-local", StringComparison.OrdinalIgnoreCase))
            {
                failed++;
                retryBuffer.Add(request);
            }
            else
            {
                synced++;
            }
        }

        foreach (var item in retryBuffer)
        {
            _pendingQueue.Enqueue(item);
        }

        var success = failed == 0;
        if (!success)
        {
            _logger.LogWarning("Policy sync complete. Synced: {Synced}, Deferred: {Deferred}.", synced, failed);
        }
        else
        {
            _logger.LogInformation("Policy sync complete. Synced: {Synced}, Deferred: {Deferred}.", synced, failed);
        }

        return new ServiceOperationResult(success, $"Policy sync complete. Synced: {synced}, Deferred: {failed}.");
    }
}
