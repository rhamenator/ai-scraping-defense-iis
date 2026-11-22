using System.Buffers;
using System.Globalization;
using DotNext.IO;
using DotNext.IO.Log;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;

namespace RedisBlocklistMiddlewareApp.Services;

/// <summary>
/// Chooses the on-disk layout of the consensus log and migrates the pre-1.2 layout (a DotNext
/// <c>DiskBasedStateMachine</c> directory named <c>log</c>) into a <c>WriteAheadLog</c> directory named <c>wal</c>.
/// A <c>layout.version</c> marker file records that the WAL layout is in use. Without it, a newer DotNext would
/// open the old directory without an error and see an empty log, so the layout must be decided explicitly.
/// </summary>
internal static class RaftLogLayout
{
    public const string LegacyDirectoryName = "log";
    public const string WalDirectoryName = "wal";
    public const string MarkerFileName = "layout.version";
    public const string MarkerValue = "wal-1";
    private const int ReadBatchSize = 256;

    /// <summary>Returns the WAL directory, migrating a legacy log first when one exists.</summary>
    public static async Task<string> PrepareAsync(string storagePath, ILogger logger, CancellationToken cancellationToken)
    {
        var markerPath = Path.Combine(storagePath, MarkerFileName);
        var walPath = Path.Combine(storagePath, WalDirectoryName);
        var legacyPath = Path.Combine(storagePath, LegacyDirectoryName);

        if (File.Exists(markerPath))
        {
            Directory.CreateDirectory(walPath);
            return walPath;
        }

        // No marker: either a fresh node, a legacy node, or an earlier migration that did not finish.
        // Any WAL directory without a marker is a partial result and is rebuilt from the legacy log.
        if (Directory.Exists(walPath))
        {
            Directory.Delete(walPath, recursive: true);
        }

        Directory.CreateDirectory(walPath);
        if (HasLegacyData(legacyPath))
        {
            var (count, skipped) = await MigrateAsync(legacyPath, walPath, cancellationToken);
            var archived = legacyPath + ".legacy-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            Directory.Move(legacyPath, archived);
            logger.LogWarning(
                "Migrated {EntryCount} committed Raft log entries from {LegacyPath} to {WalPath}; skipped {SkippedCount} uncommitted trailing entries (the leader re-sends committed entries). The old directory was kept as {ArchivePath}; delete it once the cluster is healthy.",
                count,
                legacyPath,
                walPath,
                skipped,
                archived);
        }

        var temporaryMarker = markerPath + ".tmp";
        await File.WriteAllTextAsync(temporaryMarker, MarkerValue, cancellationToken);
        File.Move(temporaryMarker, markerPath, overwrite: true);
        return walPath;
    }

    private static bool HasLegacyData(string legacyPath) =>
        Directory.Exists(legacyPath) && Directory.EnumerateFileSystemEntries(legacyPath).Any();

    private static async Task<(long Migrated, long Skipped)> MigrateAsync(string legacyPath, string walPath, CancellationToken cancellationToken)
    {
        using var legacy = new LegacyRaftLog(legacyPath);
        await legacy.InitializeAsync(cancellationToken);
        var lastIndex = legacy.LastEntryIndex;
        var committedIndex = legacy.LastCommittedEntryIndex;
        var term = legacy.Term;

        using var wal = new WriteAheadLog(new WriteAheadLog.Options { Location = walPath }, new DiscardingStateMachine());
        await wal.InitializeAsync(cancellationToken);

        // Only committed entries are migrated: DotNext 5.26's WriteAheadLog does not keep entries beyond the
        // commit point across a restart, so an uncommitted tail could not be preserved anyway.
        for (var start = 1L; start <= committedIndex; start += ReadBatchSize)
        {
            var end = Math.Min(committedIndex, start + ReadBatchSize - 1);
            var batch = await legacy.ReadAsync(new EntryCollector(), start, end, cancellationToken);
            var index = start;
            foreach (var (entryTerm, payload) in batch)
            {
                await wal.AppendAsync(new BinaryLogEntry { Content = payload, Term = entryTerm }, index, cancellationToken);
                index++;
            }
        }

        if (committedIndex > 0)
        {
            await wal.CommitAsync(committedIndex, cancellationToken);
            await wal.WaitForApplyAsync(committedIndex, cancellationToken);
        }

        // Keep the persisted Raft term so a migrated node never starts from term 0.
        await ((IPersistentState)wal).UpdateTermAsync(term, resetLastVote: false);
        await wal.FlushAsync(cancellationToken);
        return (committedIndex, lastIndex - committedIndex);
    }

    /// <summary>Reads a legacy log; it never applies entries to anything.</summary>
    private sealed class LegacyRaftLog : DiskBasedStateMachine
    {
        public LegacyRaftLog(string path)
            : base(
                path,
                recordsPerPartition: 2048,
                // The same options the pre-WAL state machine used, so the existing files are read the way they were written.
                new PersistentState.Options
                {
                    WriteMode = PersistentState.WriteMode.WriteThrough,
                    IntegrityCheck = true,
                    MaxLogEntrySize = EntryCollector.MaximumEntryBytes
                })
        {
        }

        protected override ValueTask<long?> ApplyAsync(PersistentState.LogEntry entry) => ValueTask.FromResult<long?>(null);

        protected override ValueTask<long> InstallSnapshotAsync<TSnapshot>(TSnapshot snapshot) => ValueTask.FromResult(0L);

        protected override ValueTask<IAsyncBinaryReader> BeginReadSnapshotAsync(
            SnapshotAccessToken session,
            DotNext.Buffers.MemoryAllocator<byte> allocator,
            CancellationToken token) =>
            ValueTask.FromResult(IAsyncBinaryReader.Empty);

        protected override void EndReadSnapshot(SnapshotAccessToken session)
        {
        }
    }

    /// <summary>Accepts entries during migration without touching the blocklist; the real state machine replays them later.</summary>
    private sealed class DiscardingStateMachine : IStateMachine
    {
        public ISnapshot? Snapshot => null;

        public ValueTask ReclaimGarbageAsync(long watermark, CancellationToken token) => ValueTask.CompletedTask;

        public ValueTask<long> ApplyAsync(LogEntry entry, CancellationToken token) => ValueTask.FromResult(entry.Index);
    }

    internal sealed class EntryCollector : ILogEntryConsumer<IRaftLogEntry, List<(long Term, byte[] Payload)>>
    {
        // The same ceiling the state machine enforces on a command.
        internal const int MaximumEntryBytes = 64 * 1024;

        public async ValueTask<List<(long Term, byte[] Payload)>> ReadAsync<TEntryImpl, TList>(
            TList entries,
            long? snapshotIndex,
            CancellationToken token)
            where TEntryImpl : notnull, IRaftLogEntry
            where TList : notnull, IReadOnlyList<TEntryImpl>
        {
            var result = new List<(long, byte[])>(entries.Count);
            foreach (var entry in entries)
            {
                // A null Length means "unknown", not "empty", so the payload is always read, but never past the ceiling.
                if (entry.Length is > MaximumEntryBytes)
                {
                    throw new InvalidDataException(
                        $"Legacy Raft log entry of {entry.Length} bytes exceeds the {MaximumEntryBytes}-byte limit; the migration was stopped and nothing was changed.");
                }

                await using var sink = new BoundedMemoryStream(MaximumEntryBytes);
                await entry.WriteToAsync(sink, token: token);
                var payload = sink.ToArray();
                if (entry.Length is { } declared && declared != payload.Length)
                {
                    throw new InvalidDataException(
                        $"Legacy Raft log entry declared {declared} bytes but {payload.Length} were read; the log is inconsistent, so the migration was stopped and nothing was changed.");
                }

                result.Add((entry.Term, payload));
            }

            return result;
        }
    }

    /// <summary>A write-only in-memory stream that refuses to hold more than a fixed number of bytes.</summary>
    private sealed class BoundedMemoryStream : Stream
    {
        private readonly int _limit;
        private readonly ArrayBufferWriter<byte> _buffer = new();

        public BoundedMemoryStream(int limit) => _limit = limit;

        public override bool CanRead => false;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => _buffer.WrittenCount;

        public override long Position
        {
            get => _buffer.WrittenCount;
            set => throw new NotSupportedException();
        }

        public byte[] ToArray() => _buffer.WrittenSpan.ToArray();

        public override void Write(byte[] buffer, int offset, int count) => Append(buffer.AsSpan(offset, count));

        public override void Write(ReadOnlySpan<byte> buffer) => Append(buffer);

        public override void WriteByte(byte value) => Append([value]);

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            Append(buffer.AsSpan(offset, count));
            return Task.CompletedTask;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Append(buffer.Span);
            return ValueTask.CompletedTask;
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        private void Append(ReadOnlySpan<byte> data)
        {
            // Count what is really written, whatever length the entry claimed.
            if ((long)_buffer.WrittenCount + data.Length > _limit)
            {
                throw new InvalidDataException(
                    $"Legacy Raft log entry exceeds the {_limit}-byte limit; the migration was stopped and nothing was changed.");
            }

            _buffer.Write(data);
        }
    }
}
