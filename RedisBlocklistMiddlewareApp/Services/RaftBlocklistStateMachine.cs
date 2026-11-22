using System.Buffers;
using System.Text.Json;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;

namespace RedisBlocklistMiddlewareApp.Services;

/// <summary>
/// Applies committed consensus commands to the blocklist, keeps the current set of blocks in memory, and periodically writes
/// it as a snapshot so the Raft log can be compacted (#196). A lagging follower that is sent a snapshot instead of the
/// compacted entries installs it here: the in-memory set is replaced and the materialized Redis blocklist is brought into line.
/// </summary>
internal sealed class RaftBlocklistStateMachine : SimpleStateMachine
{
    private const int MaximumCommandBytes = 64 * 1024;
    private const int SnapshotFormatVersion = 1;

    // Recent command identifiers only (#196); see BoundedCommandIdSet.
    internal const int MaximumTrackedCommands = 65_536;

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly RedisBlocklistService _blocklist;
    private readonly ILogger<RaftBlocklistStateMachine> _logger;
    private readonly BoundedCommandIdSet _appliedCommands = new(MaximumTrackedCommands);
    private readonly object _gate = new();
    private readonly Dictionary<string, BlockRecord> _blocks = new(StringComparer.Ordinal);
    private readonly int _snapshotEveryCommands;
    private int _commandsSinceSnapshot;

    public RaftBlocklistStateMachine(
        string snapshotPath,
        RedisBlocklistService blocklist,
        ILogger<RaftBlocklistStateMachine> logger,
        int snapshotEveryCommands = 1000)
        : base(new DirectoryInfo(snapshotPath))
    {
        _blocklist = blocklist;
        _logger = logger;
        _snapshotEveryCommands = Math.Max(1, snapshotEveryCommands);
    }

    /// <summary>The number of blocks currently held in memory (and written to a snapshot).</summary>
    internal int BlockCount
    {
        get
        {
            lock (_gate)
            {
                return _blocks.Count;
            }
        }
    }

    internal IReadOnlyDictionary<string, BlockRecord> CurrentBlocks()
    {
        lock (_gate)
        {
            return new Dictionary<string, BlockRecord>(_blocks, StringComparer.Ordinal);
        }
    }

    /// <summary>Applies one committed entry. Returns true when the current state should be written as a snapshot.</summary>
    protected override async ValueTask<bool> ApplyAsync(LogEntry entry, CancellationToken token)
    {
        if (entry.IsSnapshot)
        {
            await InstallSnapshotAsync(entry, token);
            return false;
        }

        // Internal no-op entries carry no blocklist command.
        if (entry.Length is null or 0)
        {
            return false;
        }

        var length = entry.Length.Value;
        if (length < 0 || length > MaximumCommandBytes)
        {
            throw new InvalidDataException($"Consensus log entry {entry.Index} has an invalid length of {length} bytes.");
        }

        var command = ReadCommand(entry, length)
            ?? throw new InvalidDataException($"Consensus log entry {entry.Index} did not contain a command.");
        Validate(command, entry.Index);

        if (!_appliedCommands.TryAdd(command.CommandId))
        {
            _logger.LogDebug(
                "Skipped duplicate consensus command {CommandId} at log index {LogIndex}.",
                command.CommandId,
                entry.Index);
            return false;
        }

        try
        {
            await MaterializeAsync(command);
            Remember(command);
            _logger.LogInformation(
                "Applied consensus {Operation} command {CommandId} for {IpAddress} at log index {LogIndex}.",
                command.Operation,
                command.CommandId,
                command.IpAddress,
                entry.Index);
        }
        catch
        {
            _appliedCommands.Remove(command.CommandId);
            throw;
        }

        return ++_commandsSinceSnapshot >= _snapshotEveryCommands && ResetSnapshotCounter();
    }

    private bool ResetSnapshotCounter()
    {
        _commandsSinceSnapshot = 0;
        return true;
    }

    private async Task MaterializeAsync(ConsensusCommand command)
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
    }

    private void Remember(ConsensusCommand command)
    {
        var key = Services.RedisBlocklistService.TryNormalizeIpAddress(command.IpAddress, out var normalized) ? normalized : command.IpAddress;
        lock (_gate)
        {
            if (string.Equals(command.Operation, ConsensusCommand.BlockOperation, StringComparison.Ordinal))
            {
                _blocks[key] = new BlockRecord(key, command.Reason, command.Signals, command.RequestedAtUtc, command.ExpiresAtUtc!.Value);
            }
            else
            {
                _blocks.Remove(key);
            }
        }
    }

    // ---- snapshots ------------------------------------------------------------------------------------------------

    /// <summary>Writes the current set of unexpired blocks.</summary>
    protected override async ValueTask PersistAsync(IAsyncBinaryWriter writer, CancellationToken token)
    {
        var payload = SerializeSnapshot(CurrentBlocks().Values, DateTimeOffset.UtcNow);
        await writer.WriteLittleEndianAsync(payload.Length, token);
        await writer.WriteAsync(payload, lengthFormat: null, token);
    }

    /// <summary>Reads a snapshot file written by <see cref="PersistAsync"/> back into memory.</summary>
    protected override async ValueTask RestoreAsync(FileInfo snapshotFile, CancellationToken token)
    {
        var bytes = await File.ReadAllBytesAsync(snapshotFile.FullName, token);
        var blocks = DeserializeSnapshot(bytes);
        lock (_gate)
        {
            _blocks.Clear();
            foreach (var block in blocks)
            {
                _blocks[block.IpAddress] = block;
            }
        }

        _logger.LogInformation("Restored {BlockCount} blocks from consensus snapshot {Snapshot}.", blocks.Count, snapshotFile.Name);
    }

    /// <summary>A snapshot sent by the leader to a follower that is behind the compacted log.</summary>
    private async ValueTask InstallSnapshotAsync(LogEntry entry, CancellationToken token)
    {
        var buffer = new ArrayBufferWriter<byte>();
        foreach (var chunk in entry.GetPayload())
        {
            buffer.Write(chunk.Span);
        }

        var blocks = DeserializeSnapshot(buffer.WrittenSpan.ToArray());
        Dictionary<string, BlockRecord> previous;
        lock (_gate)
        {
            previous = new Dictionary<string, BlockRecord>(_blocks, StringComparer.Ordinal);
            _blocks.Clear();
            foreach (var block in blocks)
            {
                _blocks[block.IpAddress] = block;
            }
        }

        // Bring the materialized blocklist into line: lift blocks that are no longer in the set, apply every block that is.
        var current = blocks.ToDictionary(block => block.IpAddress, StringComparer.Ordinal);
        foreach (var gone in previous.Keys.Where(ip => !current.ContainsKey(ip)))
        {
            await _blocklist.UnblockAsync(gone, token);
        }

        foreach (var block in blocks)
        {
            await _blocklist.BlockUntilAsync(block.IpAddress, block.Reason, block.Signals, block.ExpiresAtUtc, token);
        }

        _logger.LogWarning(
            "Installed a consensus snapshot at log index {LogIndex}: {BlockCount} blocks ({LiftedCount} lifted).",
            entry.Index,
            blocks.Count,
            previous.Keys.Count(ip => !current.ContainsKey(ip)));
    }

    internal static byte[] SerializeSnapshot(IEnumerable<BlockRecord> blocks, DateTimeOffset now)
    {
        var live = blocks.Where(block => block.ExpiresAtUtc > now).OrderBy(block => block.IpAddress, StringComparer.Ordinal).ToArray();
        return JsonSerializer.SerializeToUtf8Bytes(new SnapshotDocument(SnapshotFormatVersion, live), SerializerOptions);
    }

    internal static IReadOnlyList<BlockRecord> DeserializeSnapshot(byte[] bytes)
    {
        // The file starts with the 4-byte length of the JSON that PersistAsync wrote.
        var payload = bytes.Length >= 4 && BitConverter.ToInt32(bytes, 0) == bytes.Length - 4 ? bytes.AsSpan(4) : bytes.AsSpan();
        var document = JsonSerializer.Deserialize<SnapshotDocument>(payload, SerializerOptions)
            ?? throw new InvalidDataException("The consensus snapshot is empty.");
        if (document.Version != SnapshotFormatVersion)
        {
            throw new InvalidDataException($"The consensus snapshot has format version {document.Version}; this release reads version {SnapshotFormatVersion}.");
        }

        return document.Blocks;
    }

    // ---- commands -------------------------------------------------------------------------------------------------

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

    internal sealed record BlockRecord(
        string IpAddress,
        string Reason,
        string[] Signals,
        DateTimeOffset RequestedAtUtc,
        DateTimeOffset ExpiresAtUtc);

    private sealed record SnapshotDocument(int Version, IReadOnlyList<BlockRecord> Blocks);
}
