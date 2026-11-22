using System.Collections.Concurrent;
using System.Text.Json;
using System.Buffers;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;

namespace RedisBlocklistMiddlewareApp.Services;

/// <summary>
/// Applies committed consensus commands to the materialized Redis blocklist. The write-ahead log
/// retains every command, so this state machine keeps no snapshots of its own.
/// </summary>
internal sealed class RaftBlocklistStateMachine : IStateMachine
{
    private const int MaximumCommandBytes = 64 * 1024;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly RedisBlocklistService _blocklist;
    private readonly ILogger<RaftBlocklistStateMachine> _logger;
    private readonly ConcurrentDictionary<Guid, byte> _appliedCommands = new();

    public RaftBlocklistStateMachine(
        RedisBlocklistService blocklist,
        ILogger<RaftBlocklistStateMachine> logger)
    {
        _blocklist = blocklist;
        _logger = logger;
    }

    public ISnapshot? Snapshot => null;

    public ValueTask ReclaimGarbageAsync(long watermark, CancellationToken token) => ValueTask.CompletedTask;

    public async ValueTask<long> ApplyAsync(LogEntry entry, CancellationToken token)
    {
        // Internal no-op entries carry no blocklist command.
        if (entry.IsSnapshot || entry.Length is null or 0)
        {
            return entry.Index;
        }

        var length = entry.Length.Value;
        if (length < 0 || length > MaximumCommandBytes)
        {
            throw new InvalidDataException($"Consensus log entry {entry.Index} has an invalid length of {length} bytes.");
        }

        var command = ReadCommand(entry, length)
            ?? throw new InvalidDataException($"Consensus log entry {entry.Index} did not contain a command.");
        Validate(command, entry.Index);

        if (!_appliedCommands.TryAdd(command.CommandId, 0))
        {
            _logger.LogDebug(
                "Skipped duplicate consensus command {CommandId} at log index {LogIndex}.",
                command.CommandId,
                entry.Index);
            return entry.Index;
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
            return entry.Index;
        }
        catch
        {
            _appliedCommands.TryRemove(command.CommandId, out _);
            throw;
        }
    }

    private static ConsensusCommand? ReadCommand(LogEntry entry, long length)
    {
        if (entry.TryGetPayload(out var sequence))
        {
            var reader = new Utf8JsonReader(sequence);
            return JsonSerializer.Deserialize<ConsensusCommand>(ref reader, SerializerOptions);
        }

        var buffer = new ArrayBufferWriter<byte>((int)length);
        foreach (var chunk in entry.GetPayload())
        {
            buffer.Write(chunk.Span);
        }

        return JsonSerializer.Deserialize<ConsensusCommand>(buffer.WrittenSpan, SerializerOptions);
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
