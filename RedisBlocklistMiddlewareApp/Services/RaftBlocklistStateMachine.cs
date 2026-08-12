using System.Collections.Concurrent;
using System.Text.Json;
using DotNext.Buffers;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft;

namespace RedisBlocklistMiddlewareApp.Services;

internal sealed class RaftBlocklistStateMachine : DiskBasedStateMachine
{
    private const int MaximumCommandBytes = 64 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly RedisBlocklistService _blocklist;
    private readonly ILogger<RaftBlocklistStateMachine> _logger;
    private readonly ConcurrentDictionary<Guid, byte> _appliedCommands = new();

    public RaftBlocklistStateMachine(
        string path,
        RedisBlocklistService blocklist,
        ILogger<RaftBlocklistStateMachine> logger)
        : base(
            path,
            recordsPerPartition: 2048,
            new PersistentState.Options
            {
                WriteMode = PersistentState.WriteMode.WriteThrough,
                IntegrityCheck = true,
                MaxLogEntrySize = MaximumCommandBytes
            })
    {
        _blocklist = blocklist;
        _logger = logger;
    }

    protected override async ValueTask<long?> ApplyAsync(PersistentState.LogEntry entry)
    {
        if (entry.Length == 0)
        {
            return null;
        }
        if (entry.Length < 0 || entry.Length > MaximumCommandBytes)
        {
            throw new InvalidDataException($"Consensus log entry {entry.Index} has an invalid length of {entry.Length} bytes.");
        }

        ReadOnlyMemory<byte> payload;
        byte[]? rentedPayload = null;
        if (!entry.TryGetMemory(out payload))
        {
            rentedPayload = GC.AllocateUninitializedArray<byte>((int)entry.Length);
            await entry.GetReader().ReadAsync(rentedPayload, CancellationToken.None);
            payload = rentedPayload;
        }

        var command = JsonSerializer.Deserialize<ConsensusCommand>(payload.Span, SerializerOptions)
            ?? throw new InvalidDataException($"Consensus log entry {entry.Index} did not contain a command.");
        Validate(command, entry.Index);

        if (!_appliedCommands.TryAdd(command.CommandId, 0))
        {
            _logger.LogDebug(
                "Skipped duplicate consensus command {CommandId} at log index {LogIndex}.",
                command.CommandId,
                entry.Index);
            return null;
        }

        try
        {
            if (string.Equals(command.Operation, ConsensusCommand.BlockOperation, StringComparison.Ordinal))
            {
                var applied = await _blocklist.BlockUntilAsync(
                    command.IpAddress,
                    command.Reason,
                    command.Signals,
                    command.ExpiresAtUtc!.Value,
                    CancellationToken.None);
                if (!applied)
                {
                    throw new InvalidOperationException(
                        $"Committed block command {command.CommandId} was rejected by the materialized blocklist.");
                }
            }
            else
            {
                await _blocklist.UnblockAsync(command.IpAddress, CancellationToken.None);
            }

            _logger.LogInformation(
                "Applied consensus {Operation} command {CommandId} for {IpAddress} at log index {LogIndex}.",
                command.Operation,
                command.CommandId,
                command.IpAddress,
                entry.Index);
            return null;
        }
        catch
        {
            _appliedCommands.TryRemove(command.CommandId, out _);
            throw;
        }
    }

    protected override ValueTask<long> InstallSnapshotAsync<TSnapshot>(TSnapshot snapshot)
    {
        if (snapshot.Length is 0)
        {
            return ValueTask.FromResult(0L);
        }

        return ValueTask.FromException<long>(new InvalidOperationException(
            "This state machine retains the complete command log and does not accept non-empty snapshots."));
    }

    protected override ValueTask<IAsyncBinaryReader> BeginReadSnapshotAsync(
        SnapshotAccessToken session,
        MemoryAllocator<byte> allocator,
        CancellationToken token) =>
        ValueTask.FromResult(IAsyncBinaryReader.Empty);

    protected override void EndReadSnapshot(SnapshotAccessToken session)
    {
    }

    private static void Validate(ConsensusCommand command, long index)
    {
        if (command.CommandId == Guid.Empty ||
            !System.Net.IPAddress.TryParse(command.IpAddress, out _) ||
            (!string.Equals(command.Operation, ConsensusCommand.BlockOperation, StringComparison.Ordinal) &&
             !string.Equals(command.Operation, ConsensusCommand.UnblockOperation, StringComparison.Ordinal)) ||
            string.IsNullOrWhiteSpace(command.Reason) ||
            command.Signals is null ||
            command.Reason.Length > 1024 ||
            command.Signals.Length > 128 ||
            command.Signals.Any(signal => signal.Length > 512))
        {
            throw new InvalidDataException($"Consensus log entry {index} contains an invalid command.");
        }

        if (string.Equals(command.Operation, ConsensusCommand.BlockOperation, StringComparison.Ordinal) &&
            command.ExpiresAtUtc is null)
        {
            throw new InvalidDataException($"Consensus block entry {index} has no expiration.");
        }
    }
}
