using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using DotNext.IO;
using DotNext.IO.Log;
using DotNext.Net.Cluster.Consensus.Raft;

namespace RaftLogExporter;

/// <summary>
/// Writes the committed Raft log of a release-1.x node (DotNext 5.x) to a neutral file that releases built on DotNext 6 import.
/// </summary>
/// <remarks>
/// <para>DotNext 6 cannot read the log format this release writes, and a 5.x library cannot write a log that 6 can read, so
/// the two releases cannot share files. They share this export instead: <c>export/raft-log-v1/entries.jsonl</c> (one entry per
/// line: index, term, base64 payload) and <c>meta.json</c> (counts, the commit index, the term and a SHA-256 of the entries
/// file, which the importer checks).</para>
/// <para>The export changes nothing about how the node runs. It is written when the node starts (before the log is opened) and
/// again after a graceful stop, so a node that is stopped normally leaves an export that matches its committed log.</para>
/// </remarks>
internal static class RaftLogExport
{
    public const string LegacyDirectoryName = "log";
    public const string ExportDirectoryName = "export";
    public const string ExportVersionDirectoryName = "raft-log-v1";
    public const string EntriesFileName = "entries.jsonl";
    public const string MetaFileName = "meta.json";
    public const string FormatName = "asd-raft-log-export-v1";
    private const int ReadBatchSize = 256;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Exports the committed entries of <c>&lt;storagePath&gt;/log</c>; null when there is no log to export.</summary>
    public static async Task<RaftLogExportMeta?> ExportAsync(string storagePath, Action<string> log, CancellationToken cancellationToken)
    {
        var logPath = Path.Join(storagePath, LegacyDirectoryName);
        if (!Directory.Exists(logPath) || !Directory.EnumerateFileSystemEntries(logPath).Any())
        {
            return null;
        }

        var exportRoot = Path.Join(storagePath, ExportDirectoryName);
        var finalPath = Path.Join(exportRoot, ExportVersionDirectoryName);
        var temporaryPath = Path.Join(exportRoot, ExportVersionDirectoryName + ".tmp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryPath);
        try
        {
            using var legacy = new LegacyRaftLog(logPath);
            await legacy.InitializeAsync(cancellationToken);
            var committed = legacy.LastCommittedEntryIndex;

            var entriesPath = Path.Join(temporaryPath, EntriesFileName);
            await using (var entries = new FileStream(entriesPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            await using (var writer = new StreamWriter(entries, new System.Text.UTF8Encoding(false)))
            {
                for (var start = 1L; start <= committed; start += ReadBatchSize)
                {
                    var end = Math.Min(committed, start + ReadBatchSize - 1);
                    var batch = await legacy.ReadAsync(new EntryCollector(), start, end, cancellationToken);
                    var index = start;
                    foreach (var (term, payload) in batch)
                    {
                        await writer.WriteLineAsync(JsonSerializer.Serialize(new ExportedEntry(index, term, Convert.ToBase64String(payload)), Json));
                        index++;
                    }
                }
            }

            var meta = new RaftLogExportMeta(
                FormatName,
                Count: committed,
                CommittedIndex: committed,
                LastIndex: legacy.LastEntryIndex,
                Term: legacy.Term,
                EntriesSha256: await HashAsync(entriesPath, cancellationToken),
                WrittenAtUtc: DateTimeOffset.UtcNow);
            await File.WriteAllTextAsync(Path.Join(temporaryPath, MetaFileName), JsonSerializer.Serialize(meta, Json), cancellationToken);

            // Replace the previous export only once the new one is complete.
            if (Directory.Exists(finalPath))
            {
                Directory.Delete(finalPath, recursive: true);
            }

            Directory.Move(temporaryPath, finalPath);
            log($"Exported {meta.Count} committed Raft log entries (term {meta.Term}) to {finalPath}.");
            return meta;
        }
        catch
        {
            if (Directory.Exists(temporaryPath))
            {
                Directory.Delete(temporaryPath, recursive: true);
            }

            throw;
        }
    }

    internal static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    internal sealed record ExportedEntry(long I, long T, string P);

    // ---- reading the old DotNext 5.x log -------------------------------------------------------------------------------

    internal sealed class LegacyRaftLog : DiskBasedStateMachine
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
                        $"Legacy Raft log entry of {entry.Length} bytes exceeds the {MaximumEntryBytes}-byte limit; the export was stopped and nothing was changed.");
                }

                await using var sink = new BoundedMemoryStream(MaximumEntryBytes);
                await entry.WriteToAsync(sink, token: token);
                var payload = sink.ToArray();
                if (entry.Length is { } declared && declared != payload.Length)
                {
                    throw new InvalidDataException(
                        $"Legacy Raft log entry declared {declared} bytes but {payload.Length} were read; the log is inconsistent, so the export was stopped and nothing was changed.");
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
                    $"Legacy Raft log entry exceeds the {_limit}-byte limit; the export was stopped and nothing was changed.");
            }

            _buffer.Write(data);
        }
    }
}

/// <summary>What <c>meta.json</c> records about an export.</summary>
internal sealed record RaftLogExportMeta(
    string Format,
    long Count,
    long CommittedIndex,
    long LastIndex,
    long Term,
    string EntriesSha256,
    DateTimeOffset WrittenAtUtc);
