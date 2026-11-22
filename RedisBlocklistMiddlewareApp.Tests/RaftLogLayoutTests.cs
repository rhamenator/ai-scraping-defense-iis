using System.Text;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.Extensions.Logging.Abstractions;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class RaftLogLayoutTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "asd-raftlayout-" + Guid.NewGuid().ToString("N"));

    public RaftLogLayoutTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task FreshNode_GetsEmptyWalAndMarker()
    {
        var walPath = await RaftLogLayout.PrepareAsync(_root, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(Path.Join(_root, "wal"), walPath);
        Assert.Equal("wal-1", File.ReadAllText(Path.Join(_root, "layout.version")));
        var (last, committed, _) = await OpenWalAsync(walPath);
        Assert.Equal(0, last);
        Assert.Equal(0, committed);
    }

    [Fact]
    public async Task LegacyLog_IsMigrated_WithEntriesTermAndReplay()
    {
        var payloads = new[] { "{\"n\":1}", "", "{\"n\":3}", "{\"n\":4}" };
        await WriteLegacyLogAsync(Path.Join(_root, "log"), payloads, commitThrough: 3);

        var walPath = await RaftLogLayout.PrepareAsync(_root, NullLogger.Instance, CancellationToken.None);

        Assert.Equal("wal-1", File.ReadAllText(Path.Join(_root, "layout.version")));
        Assert.False(Directory.Exists(Path.Join(_root, "log")));
        Assert.Single(Directory.GetDirectories(_root, "log.legacy-*"));

        var (last, committed, _) = await OpenWalAsync(walPath);
        // The uncommitted fourth entry is not migrated (see RaftLogLayout); committed entries all are.
        Assert.Equal(3, last);
        Assert.Equal(3, committed);

        // The migrated entries keep their order, terms and exact bytes, including the empty one.
        await using var wal = new WriteAheadLog(new WriteAheadLog.Options { Location = walPath }, new RecordingStateMachine());
        await wal.InitializeAsync();
        var entries = await wal.ReadAsync(new RaftLogLayout.EntryCollector(), 1, 3, CancellationToken.None);
        Assert.Equal(
            new[] { "{\"n\":1}", "", "{\"n\":3}" },
            entries.Select(e => Encoding.UTF8.GetString(e.Payload)).ToArray());
        Assert.All(entries, e => Assert.Equal(2, e.Term));
    }

    [Fact]
    public async Task AlreadyMigratedNode_IsLeftAlone()
    {
        await WriteLegacyLogAsync(Path.Join(_root, "log"), ["{\"n\":1}"], commitThrough: 1);
        var first = await RaftLogLayout.PrepareAsync(_root, NullLogger.Instance, CancellationToken.None);
        var archived = Directory.GetDirectories(_root, "log.legacy-*").Single();

        var second = await RaftLogLayout.PrepareAsync(_root, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(first, second);
        Assert.Equal(archived, Directory.GetDirectories(_root, "log.legacy-*").Single());
        var (last, committed, _) = await OpenWalAsync(second);
        Assert.Equal(1, last);
        Assert.Equal(1, committed);
    }

    [Fact]
    public async Task PartialWalWithoutMarker_IsRebuiltFromTheLegacyLog()
    {
        await WriteLegacyLogAsync(Path.Join(_root, "log"), ["{\"n\":1}", "{\"n\":2}"], commitThrough: 2);
        Directory.CreateDirectory(Path.Join(_root, "wal"));
        File.WriteAllText(Path.Join(_root, "wal", "leftover"), "partial migration");

        var walPath = await RaftLogLayout.PrepareAsync(_root, NullLogger.Instance, CancellationToken.None);

        Assert.False(File.Exists(Path.Join(walPath, "leftover")));
        var (last, committed, _) = await OpenWalAsync(walPath);
        Assert.Equal(2, last);
        Assert.Equal(2, committed);
    }

    [Theory]
    [InlineData(100 * 1024, 100 * 1024)] // declared and delivered too large
    [InlineData(10, 100 * 1024)]          // claims to be small, delivers more than the limit
    [InlineData(null, 100 * 1024)]        // length unknown, delivers more than the limit
    public async Task EntryCollector_RefusesEntriesLargerThanTheLimit(int? declared, int actual)
    {
        var entries = new List<FakeEntry> { new(declared, actual) };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new RaftLogLayout.EntryCollector().ReadAsync<FakeEntry, List<FakeEntry>>(entries, null, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task EntryCollector_RefusesEntriesWhoseDeclaredLengthDiffersFromTheBytesRead()
    {
        var entries = new List<FakeEntry> { new(declared: 50, actual: 10) };

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new RaftLogLayout.EntryCollector().ReadAsync<FakeEntry, List<FakeEntry>>(entries, null, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task EntryCollector_ReadsEntriesWithinTheLimit()
    {
        var entries = new List<FakeEntry> { new(10, 10), new(null, 20), new(0, 0) };

        var result = await new RaftLogLayout.EntryCollector()
            .ReadAsync<FakeEntry, List<FakeEntry>>(entries, null, CancellationToken.None);

        Assert.Equal(new[] { 10, 20, 0 }, result.Select(r => r.Payload.Length).ToArray());
    }

    private sealed class FakeEntry(int? declared, int actual) : IRaftLogEntry
    {
        public long Term => 1;

        public DateTimeOffset Timestamp => DateTimeOffset.UnixEpoch;

        public bool IsSnapshot => false;

        public long? Length => declared;

        public bool IsReusable => true;

        public ValueTask WriteToAsync<TWriter>(TWriter writer, CancellationToken token)
            where TWriter : IAsyncBinaryWriter =>
            writer.WriteAsync(new byte[actual], null, token);
    }

    private static async Task WriteLegacyLogAsync(string path, string[] payloads, long commitThrough, int maxEntryBytes = 64 * 1024)
    {
        Directory.CreateDirectory(path);
        using var legacy = new LegacyWriter(path, maxEntryBytes);
        await legacy.InitializeAsync();
        foreach (var payload in payloads)
        {
            await legacy.AppendAsync(
                new BinaryLogEntry { Content = Encoding.UTF8.GetBytes(payload), Term = 2 },
                CancellationToken.None);
        }

        await legacy.CommitAsync(commitThrough, CancellationToken.None);
    }

    private static async Task<(long Last, long Committed, List<string> Applied)> OpenWalAsync(string walPath)
    {
        var machine = new RecordingStateMachine();
        await using var wal = new WriteAheadLog(new WriteAheadLog.Options { Location = walPath }, machine);
        await wal.InitializeAsync();
        if (wal.LastCommittedEntryIndex > 0)
        {
            await wal.WaitForApplyAsync(wal.LastCommittedEntryIndex, CancellationToken.None);
        }

        return (wal.LastEntryIndex, wal.LastCommittedEntryIndex, machine.Applied);
    }

    private sealed class RecordingStateMachine : IStateMachine
    {
        public List<string> Applied { get; } = [];

        public ISnapshot? Snapshot => null;

        public ValueTask ReclaimGarbageAsync(long watermark, CancellationToken token) => ValueTask.CompletedTask;

        public ValueTask<long> ApplyAsync(LogEntry entry, CancellationToken token)
        {
            var text = string.Empty;
            if (entry.TryGetPayload(out var sequence))
            {
                text = Encoding.UTF8.GetString(sequence);
            }

            Applied.Add(text);
            return ValueTask.FromResult(entry.Index);
        }
    }

    private sealed class LegacyWriter : DiskBasedStateMachine
    {
        public LegacyWriter(string path, int maxEntryBytes = 64 * 1024)
            : base(path, recordsPerPartition: 2048, new PersistentState.Options
            {
                WriteMode = PersistentState.WriteMode.WriteThrough,
                IntegrityCheck = true,
                MaxLogEntrySize = maxEntryBytes
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
}
