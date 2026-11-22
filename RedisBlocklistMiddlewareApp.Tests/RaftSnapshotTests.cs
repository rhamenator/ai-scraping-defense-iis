using System.Text.Json;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using Microsoft.Extensions.Logging.Abstractions;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class RaftSnapshotTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly DateTimeOffset Far = new(2099, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Join(Path.GetTempPath(), "asd-raft-snapshot-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---- the real release 1.2 log ------------------------------------------------------------------------------------

    [Fact]
    public async Task AnImportedDotNext5LogReplaysIntoTheNewStateMachine()
    {
        ImportFixture();
        var (walPath, snapshotPath) = await RaftLogLayout.PrepareAsync(_root, NullLogger.Instance, CancellationToken.None);
        var blocklist = new FakeBlocklist();

        await using var node = await Node.OpenAsync(walPath, snapshotPath, blocklist, snapshotEvery: 1000);
        await node.Wal.WaitForApplyAsync(110);

        // 100 blocks (10.1.0.1-100), then unblocks for 10.1.0.1-10: ninety remain.
        Assert.Equal(90, node.Machine.BlockCount);
        Assert.Equal(90, blocklist.Blocked.Count);
        Assert.False(blocklist.Blocked.ContainsKey("10.1.0.5"));
        Assert.True(blocklist.Blocked.ContainsKey("10.1.0.11"));
        Assert.True(blocklist.Blocked.ContainsKey("10.1.0.100"));
        Assert.Equal(110, node.Wal.LastEntryIndex);
    }

    [Fact]
    public async Task AfterTheFirstStartWithTheNewReleaseTheNodeKeepsWorkingAndCompacts()
    {
        ImportFixture();
        var (walPath, snapshotPath) = await RaftLogLayout.PrepareAsync(_root, NullLogger.Instance, CancellationToken.None);
        var blocklist = new FakeBlocklist();

        await using (var node = await Node.OpenAsync(walPath, snapshotPath, blocklist, snapshotEvery: 10))
        {
            await node.Wal.WaitForApplyAsync(110);
            for (var i = 0; i < 30; i++)
            {
                await node.AppendBlockAsync(111 + i, $"10.2.0.{i + 1}");
            }

            await node.Wal.WaitForApplyAsync(140);
            Assert.Equal(120, node.Machine.BlockCount);
        }

        Assert.NotEmpty(Directory.EnumerateFiles(snapshotPath));
    }

    // ---- snapshots and compaction ------------------------------------------------------------------------------------

    [Fact]
    public async Task ASnapshotIsWrittenEveryNCommandsAndTheLogBeforeItIsCompacted()
    {
        var withSnapshots = await FillAsync(Path.Join(_root, "with"), commands: 400, snapshotEvery: 20);
        var without = await FillAsync(Path.Join(_root, "without"), commands: 400, snapshotEvery: int.MaxValue);

        Assert.True(withSnapshots.SnapshotFiles >= 1, "a snapshot was written");
        Assert.Equal(0, without.SnapshotFiles);
        Assert.True(
            withSnapshots.LogBytes < without.LogBytes,
            $"compacted {withSnapshots} vs full {without}");
        Assert.Equal(400, withSnapshots.Blocks);
        Assert.Equal(400, without.Blocks);
    }

    [Fact]
    public async Task ARestartRestoresFromTheSnapshotAndContinues()
    {
        var walPath = Path.Join(_root, "wal");
        var snapshotPath = Path.Join(_root, "snapshots");
        Directory.CreateDirectory(walPath);
        Directory.CreateDirectory(snapshotPath);

        await using (var first = await Node.OpenAsync(walPath, snapshotPath, new FakeBlocklist(), snapshotEvery: 15))
        {
            for (var i = 0; i < 60; i++)
            {
                await first.AppendBlockAsync(i + 1, $"10.3.0.{i + 1}");
            }

            await first.Wal.WaitForApplyAsync(60);
        }

        var secondBlocklist = new FakeBlocklist();
        await using var second = await Node.OpenAsync(walPath, snapshotPath, secondBlocklist, snapshotEvery: 15);
        await second.Wal.WaitForApplyAsync(60);

        Assert.Equal(60, second.Machine.BlockCount);
        Assert.Equal(60, second.Wal.LastEntryIndex);
        // Entries covered by the snapshot are not replayed into Redis again.
        Assert.True(secondBlocklist.Blocked.Count < 60, $"only the tail after the snapshot was re-applied ({secondBlocklist.Blocked.Count})");

        await second.AppendBlockAsync(61, "10.3.1.1");
        await second.Wal.WaitForApplyAsync(61);
        Assert.Equal(61, second.Machine.BlockCount);
    }

    [Fact]
    public void ASnapshotRoundTripsAndDropsExpiredBlocks()
    {
        var now = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        var blocks = new[]
        {
            new RaftBlocklistStateMachine.BlockRecord("10.0.0.1", "a", ["s1", "s2"], now, now.AddHours(1)),
            new RaftBlocklistStateMachine.BlockRecord("10.0.0.2", "b", [], now, now.AddHours(-1)),
            new RaftBlocklistStateMachine.BlockRecord("2001:db8::1", "c", ["x"], now, Far)
        };

        var bytes = RaftBlocklistStateMachine.SerializeSnapshot(blocks, now);
        var restored = RaftBlocklistStateMachine.DeserializeSnapshot(bytes);

        Assert.Equal(["10.0.0.1", "2001:db8::1"], restored.Select(b => b.IpAddress).ToArray());
        Assert.Equal(["s1", "s2"], restored[0].Signals);
        Assert.Equal(Far, restored[1].ExpiresAtUtc);
    }

    [Fact]
    public void ASnapshotWithAnUnknownFormatVersionIsRefused()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { version = 99, blocks = Array.Empty<object>() }, Json);

        Assert.Throws<InvalidDataException>(() => RaftBlocklistStateMachine.DeserializeSnapshot(bytes));
    }

    [Fact]
    public void ACorruptSnapshotIsRefusedNotPartlyLoaded()
    {
        Assert.ThrowsAny<Exception>(() => RaftBlocklistStateMachine.DeserializeSnapshot("not json"u8.ToArray()));
        Assert.ThrowsAny<Exception>(() => RaftBlocklistStateMachine.DeserializeSnapshot([]));
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private void ImportFixture()
    {
        RaftStorageMigrationTests.CopyExport(_root);
        Directory.CreateDirectory(Path.Join(_root, "log"));
        File.WriteAllText(Path.Join(_root, "log", "0"), "placeholder for the DotNext 5 files");
    }

    private static async Task<(int SnapshotFiles, long LogBytes, int Blocks)> FillAsync(string dir, int commands, int snapshotEvery)
    {
        var walPath = Path.Join(dir, "wal");
        var snapshotPath = Path.Join(dir, "snapshots");
        Directory.CreateDirectory(walPath);
        Directory.CreateDirectory(snapshotPath);
        int blocks;
        await using (var node = await Node.OpenAsync(walPath, snapshotPath, new FakeBlocklist(), snapshotEvery))
        {
            for (var i = 0; i < commands; i++)
            {
                await node.AppendBlockAsync(i + 1, $"10.{(i / 250) + 10}.0.{(i % 250) + 1}");
            }

            await node.Wal.WaitForApplyAsync(commands);
            blocks = node.Machine.BlockCount;
        }

        // A restart is when the log drops what a snapshot already covers.
        await using (var reopened = await Node.OpenAsync(walPath, snapshotPath, new FakeBlocklist(), snapshotEvery))
        {
            await reopened.Wal.WaitForApplyAsync(commands);
            await Task.Delay(1000);
        }

        var bytes = Directory.EnumerateFiles(walPath, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
        return (Directory.EnumerateFiles(snapshotPath).Count(), bytes, blocks);
    }

    private sealed class Node : IAsyncDisposable
    {
        private Node(WriteAheadLog wal, RaftBlocklistStateMachine machine)
        {
            Wal = wal;
            Machine = machine;
        }

        public WriteAheadLog Wal { get; }

        public RaftBlocklistStateMachine Machine { get; }

        public static async Task<Node> OpenAsync(string walPath, string snapshotPath, IBlocklistService blocklist, int snapshotEvery)
        {
            var machine = new RaftBlocklistStateMachine(snapshotPath, blocklist, NullLogger<RaftBlocklistStateMachine>.Instance, snapshotEvery);
            await machine.RestoreAsync(CancellationToken.None);
            var wal = new WriteAheadLog(new WriteAheadLog.Options { Location = walPath, ChunkSize = 4096 }, machine);
            await wal.InitializeAsync();
            return new Node(wal, machine);
        }

        public async Task AppendBlockAsync(long index, string ip)
        {
            var command = new ConsensusCommand(Guid.NewGuid(), ConsensusCommand.BlockOperation, ip, "test", ["t"], DateTimeOffset.UtcNow, Far);
            await Wal.AppendAsync(new BinaryLogEntry { Content = JsonSerializer.SerializeToUtf8Bytes(command, Json), Term = 1 }, index);
            await Wal.CommitAsync(index);
        }

        public async ValueTask DisposeAsync()
        {
            await Wal.FlushAsync();
            await Wal.DisposeAsync();
            await Machine.DisposeAsync();
        }
    }

    private sealed class FakeBlocklist : IBlocklistService
    {
        public Dictionary<string, DateTimeOffset> Blocked { get; } = new(StringComparer.Ordinal);

        public Task<bool> IsBlockedAsync(string ipAddress, CancellationToken cancellationToken) => Task.FromResult(Blocked.ContainsKey(ipAddress));

        public Task<bool> BlockAsync(string ipAddress, string reason, IReadOnlyCollection<string> signals, CancellationToken cancellationToken)
        {
            Blocked.TryAdd(ipAddress, Far);
            return Task.FromResult(true);
        }

        public Task<bool> BlockUntilAsync(string ipAddress, string reason, IReadOnlyCollection<string> signals, DateTimeOffset expiresAtUtc, CancellationToken cancellationToken)
        {
            Blocked[ipAddress] = expiresAtUtc;
            return Task.FromResult(true);
        }

        public Task UnblockAsync(string ipAddress, CancellationToken cancellationToken)
        {
            Blocked.Remove(ipAddress);
            return Task.CompletedTask;
        }
    }
}
