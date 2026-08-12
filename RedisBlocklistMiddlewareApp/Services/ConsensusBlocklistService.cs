namespace RedisBlocklistMiddlewareApp.Services;

public sealed class ConsensusBlocklistService : IBlocklistService
{
    private readonly RedisBlocklistService _inner;
    private readonly BlocklistConsensusCoordinator _consensus;

    public ConsensusBlocklistService(
        RedisBlocklistService inner,
        BlocklistConsensusCoordinator consensus)
    {
        _inner = inner;
        _consensus = consensus;
    }

    public Task<bool> IsBlockedAsync(string ipAddress, CancellationToken cancellationToken) =>
        _inner.IsBlockedAsync(ipAddress, cancellationToken);

    public async Task<bool> BlockAsync(
        string ipAddress,
        string reason,
        IReadOnlyCollection<string> signals,
        CancellationToken cancellationToken)
    {
        if (!_inner.CanBlock(ipAddress))
        {
            return false;
        }

        try
        {
            var committed = await _consensus.ReplicateAsync(
                ConsensusCommand.Block(
                    ipAddress,
                    reason,
                    signals,
                    _inner.GetBlockExpirationUtc()),
                cancellationToken);
            if (!committed)
            {
                throw new ConsensusUnavailableException(
                    "The block command could not be committed by a Raft quorum.");
            }
            return true;
        }
        catch (ConsensusUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new ConsensusUnavailableException(
                "The block command could not reach the Raft leader.",
                exception);
        }
    }

    public async Task UnblockAsync(string ipAddress, CancellationToken cancellationToken)
    {
        try
        {
            var committed = await _consensus.ReplicateAsync(
                ConsensusCommand.Unblock(ipAddress),
                cancellationToken);
            if (!committed)
            {
                throw new ConsensusUnavailableException(
                    "The unblock command could not be committed by a Raft quorum.");
            }
        }
        catch (ConsensusUnavailableException)
        {
            throw;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new ConsensusUnavailableException(
                "The unblock command could not reach the Raft leader.",
                exception);
        }
    }
}

public sealed class ConsensusUnavailableException : Exception
{
    public ConsensusUnavailableException(string message)
        : base(message)
    {
    }

    public ConsensusUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
