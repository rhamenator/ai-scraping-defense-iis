using System.Collections.Concurrent;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public class PolicyService : IPolicyService
{
    private const int MaxPendingQueueSize = 1000;

    private readonly IDefenseEngineClient _defenseEngineClient;
    private readonly ILogger<PolicyService> _logger;
    private readonly ConcurrentQueue<PolicySubmissionRequest> _pendingQueue = new();
    private readonly object _enqueueLock = new();

    public PolicyService(IDefenseEngineClient defenseEngineClient, ILogger<PolicyService> logger)
    {
        _defenseEngineClient = defenseEngineClient;
        _logger = logger;
    }

    public async Task<PolicySubmissionResponse> PushPolicyAsync(PolicySubmissionRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _defenseEngineClient.SubmitPolicyAsync(request, cancellationToken);
        if (string.Equals(response.Status, "queued-local", StringComparison.OrdinalIgnoreCase))
        {
            lock (_enqueueLock)
            {
                if (_pendingQueue.Count >= MaxPendingQueueSize)
                {
                    _logger.LogError(
                        "Policy queue is full ({MaxSize}); dropping policy {PolicyName}.",
                        MaxPendingQueueSize,
                        request.PolicyName);
                    return response with { Status = "dropped-queue-full" };
                }

                _pendingQueue.Enqueue(request);
            }

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
            var result = await _defenseEngineClient.SubmitPolicyAsync(request, cancellationToken);
            if (string.Equals(result.Status, "queued-local", StringComparison.OrdinalIgnoreCase))
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
        var message = $"Policy sync complete. Synced: {synced}, Deferred: {failed}.";
        if (!success)
        {
            _logger.LogWarning(message);
        }
        else
        {
            _logger.LogInformation(message);
        }

        return new ServiceOperationResult(success, message);
    }
}
