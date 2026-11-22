using System.Text;
using System.Text.Json;
using DotNext.IO;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.Extensions.Logging.Abstractions;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class RaftLogExportTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "asd-raftexport-" + Guid.NewGuid().ToString("N"));

    public RaftLogExportTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task TheCommittedEntriesAreExportedWithTheirIndexTermAndExactBytes()
    {
        var payloads = new[] { "{\"n\":1}", "", "{\"n\":3}", "{\"n\":4}" };
        await WriteLegacyLogAsync(Path.Join(_root, "log"), payloads, commitThrough: 3, term: 3);

        var meta = await RaftLogExport.ExportAsync(_root, NullLogger.Instance, CancellationToken.None);

        Assert.NotNull(meta);
        Assert.Equal(RaftLogExport.FormatName, meta!.Format);
        Assert.Equal(3, meta.Count);
        Assert.Equal(3, meta.CommittedIndex);
        Assert.Equal(4, meta.LastIndex);
        Assert.Equal(3, meta.Term);
        var entries = ReadEntries();
        Assert.Equal([1L, 2L, 3L], entries.Select(e => e.I).ToArray());
        Assert.All(entries, e => Assert.Equal(3, e.T));
        Assert.Equal(["{\"n\":1}", "", "{\"n\":3}"], entries.Select(e => Encoding.UTF8.GetString(Convert.FromBase64String(e.P))).ToArray());
    }

    [Fact]
    public async Task TheMetaFileHoldsTheHashOfTheEntriesFileSoAnImporterCanCheckIt()
    {
        await WriteLegacyLogAsync(Path.Join(_root, "log"), ["a", "b"], commitThrough: 2);
        await RaftLogExport.ExportAsync(_root, NullLogger.Instance, CancellationToken.None);

        var meta = JsonSerializer.Deserialize<RaftLogExportMeta>(File.ReadAllText(MetaPath()), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        Assert.Equal(await RaftLogExport.HashAsync(EntriesPath(), CancellationToken.None), meta.EntriesSha256);
        Assert.Matches("^[0-9a-f]{64}$", meta.EntriesSha256);
    }

    [Fact]
    public async Task NoLogOrAnEmptyLogDirectoryMeansNothingToExport()
    {
        Assert.Null(await RaftLogExport.ExportAsync(_root, NullLogger.Instance, CancellationToken.None));

        Directory.CreateDirectory(Path.Join(_root, "log"));
        Assert.Null(await RaftLogExport.ExportAsync(_root, NullLogger.Instance, CancellationToken.None));
        Assert.False(Directory.Exists(Path.Join(_root, "export")));
    }

    [Fact]
    public async Task ExportingAgainReplacesTheEarlierExportAndLeavesNoTemporaryFiles()
    {
        await WriteLegacyLogAsync(Path.Join(_root, "log"), ["a", "b", "c"], commitThrough: 2);
        await RaftLogExport.ExportAsync(_root, NullLogger.Instance, CancellationToken.None);
        Assert.Equal(2, ReadEntries().Count);

        using (var legacy = new RaftLogExport.LegacyRaftLog(Path.Join(_root, "log")))
        {
            await legacy.InitializeAsync();
            await legacy.CommitAsync(3, CancellationToken.None);
        }

        await RaftLogExport.ExportAsync(_root, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(3, ReadEntries().Count);
        Assert.Single(Directory.EnumerateDirectories(Path.Join(_root, "export")));
    }

    [Fact]
    public async Task AnExportThatFailsLeavesThePreviousGoodExportUntouched()
    {
        await WriteLegacyLogAsync(Path.Join(_root, "log"), ["a", "b"], commitThrough: 2);
        await RaftLogExport.ExportAsync(_root, NullLogger.Instance, CancellationToken.None);
        var before = File.ReadAllText(EntriesPath());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => RaftLogExport.ExportAsync(_root, NullLogger.Instance, cancelled.Token));

        Assert.Equal(before, File.ReadAllText(EntriesPath()));
        Assert.Single(Directory.EnumerateDirectories(Path.Join(_root, "export")));
    }

    /// <summary>
    /// Not a test: with <c>ASD_RAFT_FIXTURE_OUT</c> set, writes the old-format log and its export that the DotNext 6 release's
    /// import tests use as fixtures (see tools/fixtures/README.md). Does nothing otherwise.
    /// </summary>
    [Fact]
    public async Task WriteFixtureWhenAsked()
    {
        var output = Environment.GetEnvironmentVariable("ASD_RAFT_FIXTURE_OUT");
        if (string.IsNullOrWhiteSpace(output))
        {
            return;
        }

        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var expires = new DateTimeOffset(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var requested = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        var payloads = Enumerable.Range(0, 110).Select(i =>
        {
            var block = i < 100;
            return JsonSerializer.Serialize(
                new
                {
                    CommandId = new Guid(i + 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1),
                    Operation = block ? "block" : "unblock",
                    IpAddress = $"10.1.0.{(block ? i : i - 100) + 1}",
                    Reason = block ? "fixture_block" : "manual_unblock",
                    Signals = block ? new[] { "fixture" } : Array.Empty<string>(),
                    RequestedAtUtc = requested,
                    ExpiresAtUtc = block ? (DateTimeOffset?)expires : null
                },
                json);
        }).ToArray();
        await WriteLegacyLogAsync(Path.Join(_root, "log"), payloads, commitThrough: 110, term: 3);
        await RaftLogExport.ExportAsync(_root, NullLogger.Instance, CancellationToken.None);

        if (Directory.Exists(output))
        {
            Directory.Delete(output, recursive: true);
        }

        CopyDirectory(_root, output);
    }

    private string EntriesPath() => Path.Join(_root, "export", "raft-log-v1", "entries.jsonl");

    private string MetaPath() => Path.Join(_root, "export", "raft-log-v1", "meta.json");

    private List<RaftLogExport.ExportedEntry> ReadEntries() =>
        File.ReadAllLines(EntriesPath())
            .Select(line => JsonSerializer.Deserialize<RaftLogExport.ExportedEntry>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToList();

    private static async Task WriteLegacyLogAsync(string path, string[] payloads, long commitThrough, long term = 2)
    {
        Directory.CreateDirectory(path);
        using var legacy = new LegacyWriter(path);
        await legacy.InitializeAsync();
        foreach (var payload in payloads)
        {
            await legacy.AppendAsync(new BinaryLogEntry { Content = Encoding.UTF8.GetBytes(payload), Term = term }, CancellationToken.None);
        }

        await legacy.CommitAsync(commitThrough, CancellationToken.None);
        await ((IPersistentState)legacy).UpdateTermAsync(term, resetLastVote: false);
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Join(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Join(target, Path.GetFileName(directory)));
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
