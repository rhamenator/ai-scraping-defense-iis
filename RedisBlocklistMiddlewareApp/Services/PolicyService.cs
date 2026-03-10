using System.Collections.Concurrent;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public class PolicyService : IPolicyService
{
    private const int MaxPendingQueueSize = 1000;

    private readonly IDefenseEngineClient _defenseEngineClient;
    private readonly ILogger<PolicyService> _logger;
    private readonly ConcurrentQueue<PolicySubmissionRequest> _pendingQueue = new();
    private int _pendingCount = 0;

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
            if (!TryEnqueue(request))
            {
                _logger.LogError(
                    "Policy queue is full ({MaxSize}); dropping policy {PolicyName}.",
                    MaxPendingQueueSize,
                    request.PolicyName);
                return response with { Status = "dropped-queue-full" };
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
            Interlocked.Decrement(ref _pendingCount);
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

        var dropped = 0;
        foreach (var item in retryBuffer)
        {
            if (TryEnqueue(item))
            {
                // re-queued for next sync cycle
            }
            else
            {
                dropped++;
            }
        }

        var success = failed == 0;
        if (!success)
        {
            _logger.LogWarning(
                "Policy sync complete. Synced: {Synced}, Deferred: {Deferred}, Dropped: {Dropped}.",
                synced, failed, dropped);
        }
        else
        {
            _logger.LogInformation(
                "Policy sync complete. Synced: {Synced}, Deferred: {Deferred}, Dropped: {Dropped}.",
                synced, failed, dropped);
        }

        return new ServiceOperationResult(success, $"Policy sync complete. Synced: {synced}, Deferred: {failed}, Dropped: {dropped}.");
    }

    private bool TryEnqueue(PolicySubmissionRequest request)
    {
        // Increment first to reserve a slot. If another thread dequeues between the increment
        // and the Enqueue call, _pendingCount will temporarily overcount by one until Enqueue
        // completes—this is a conservative reservation and does not cause permanent drift.
        var newCount = Interlocked.Increment(ref _pendingCount);
        if (newCount > MaxPendingQueueSize)
        {
            Interlocked.Decrement(ref _pendingCount);
            return false;
        }

        _pendingQueue.Enqueue(request);
        return true;
    }
}
