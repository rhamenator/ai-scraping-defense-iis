using Microsoft.Extensions.Logging.Abstractions;
using RedisBlocklistMiddlewareApp.Services;
using RedisBlocklistMiddlewareApp.Services.Migrations;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class RaftStorageMigrationTests : IDisposable
{
    private readonly string _root = Path.Join(Path.GetTempPath(), "asd-raft-migration-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    // ---- the Raft layout ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task AFreshNodeGetsTheCurrentLayout()
    {
        var (wal, snapshots) = await RaftLogLayout.PrepareAsync(_root, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(RaftLogLayout.CurrentVersion, File.ReadAllText(Path.Join(_root, RaftLogLayout.MarkerFileName)));
        Assert.True(Directory.Exists(wal));
        Assert.True(Directory.Exists(snapshots));
    }

    [Fact]
    public async Task ADotNext5LogWithItsExportIsImportedAndTheOldLogIsKept()
    {
        CopyExport(_root);
        Directory.CreateDirectory(Path.Join(_root, "log"));
        File.WriteAllText(Path.Join(_root, "log", "0"), "old format");

        var (wal, _) = await RaftLogLayout.PrepareAsync(_root, NullLogger.Instance, CancellationToken.None);

        Assert.Equal("wal-1", File.ReadAllText(Path.Join(_root, RaftLogLayout.MarkerFileName)));
        Assert.False(Directory.Exists(Path.Join(_root, "log")));
        Assert.Equal("old format", File.ReadAllText(Path.Join(Assert.Single(Directory.EnumerateDirectories(_root, "log.imported-*")), "0")));
        Assert.False(Directory.Exists(Path.Join(_root, "wal.importing")));
        Assert.True(Directory.Exists(wal));
    }

    [Fact]
    public async Task RunningItAgainDoesNothingMore()
    {
        CopyExport(_root);
        Directory.CreateDirectory(Path.Join(_root, "log"));
        File.WriteAllText(Path.Join(_root, "log", "0"), "x");
        await RaftLogLayout.PrepareAsync(_root, NullLogger.Instance, CancellationToken.None);

        await RaftLogLayout.PrepareAsync(_root, NullLogger.Instance, CancellationToken.None);

        Assert.Single(Directory.EnumerateDirectories(_root, "log.imported-*"));
    }

    [Fact]
    public async Task AnInterruptedImportStartsOverFromTheExport()
    {
        CopyExport(_root);
        Directory.CreateDirectory(Path.Join(_root, "log"));
        File.WriteAllText(Path.Join(_root, "log", "0"), "x");
        // What an import killed after moving the log into place but before writing the marker leaves behind.
        Directory.CreateDirectory(Path.Join(_root, "wal"));
        File.WriteAllText(Path.Join(_root, "wal", "junk"), "partial");
        Directory.CreateDirectory(Path.Join(_root, "wal.importing"));

        var (wal, _) = await RaftLogLayout.PrepareAsync(_root, NullLogger.Instance, CancellationToken.None);

        Assert.False(File.Exists(Path.Join(wal, "junk")));
        Assert.Equal("wal-1", File.ReadAllText(Path.Join(_root, RaftLogLayout.MarkerFileName)));
    }

    [Fact]
    public async Task ADotNext5LogWithoutAnExportIsRefusedWithInstructionsAndNothingIsChanged()
    {
        Directory.CreateDirectory(Path.Join(_root, "log"));
        File.WriteAllText(Path.Join(_root, "log", "0"), "old format");

        var error = await Assert.ThrowsAsync<StorageMigrationException>(() =>
            RaftLogLayout.PrepareAsync(_root, NullLogger.Instance, CancellationToken.None));

        Assert.Contains("RaftLogExporter", error.Message);
        Assert.False(File.Exists(Path.Join(_root, RaftLogLayout.MarkerFileName)));
        Assert.Equal("old format", File.ReadAllText(Path.Join(_root, "log", "0")));
    }

    [Fact]
    public async Task AnExportWhoseHashDoesNotMatchIsRefusedAndNothingIsChanged()
    {
        CopyExport(_root);
        Directory.CreateDirectory(Path.Join(_root, "log"));
        File.WriteAllText(Path.Join(_root, "log", "0"), "x");
        File.AppendAllText(Path.Join(_root, "export", "raft-log-v1", "entries.jsonl"), "\n");
        var path = Path.Join(_root, "export", "raft-log-v1", "entries.jsonl");
        var lines = File.ReadAllLines(path);
        File.WriteAllLines(path, lines.Take(lines.Length - 2));

        var error = await Assert.ThrowsAsync<StorageMigrationException>(() =>
            RaftLogLayout.PrepareAsync(_root, NullLogger.Instance, CancellationToken.None));

        Assert.Contains("SHA-256", error.Message);
        Assert.False(File.Exists(Path.Join(_root, RaftLogLayout.MarkerFileName)));
        Assert.True(Directory.Exists(Path.Join(_root, "log")));
        Assert.False(Directory.Exists(Path.Join(_root, "wal")));
    }

    [Fact]
    public async Task DataMarkedWithANewerLayoutIsRefusedNotGuessedAt()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Join(_root, RaftLogLayout.MarkerFileName), "wal-9");

        var error = await Assert.ThrowsAsync<StorageMigrationException>(() =>
            RaftLogLayout.PrepareAsync(_root, NullLogger.Instance, CancellationToken.None));

        Assert.Contains("wal-9", error.Message);
        Assert.Equal("wal-9", File.ReadAllText(Path.Join(_root, RaftLogLayout.MarkerFileName)));
    }

    // ---- the reusable runner -----------------------------------------------------------------------------------------

    [Fact]
    public async Task StepsRunInOrderToTheCurrentVersion_EachBackedUpFirst()
    {
        Directory.CreateDirectory(Path.Join(_root, "data"));
        File.WriteAllText(Path.Join(_root, "data", "a"), "1");
        File.WriteAllText(Path.Join(_root, "marker"), "v1");
        var order = new List<string>();
        StorageMigrationStep Step(string from, string to) =>
            new(from, to, $"{from} to {to}", ["data"], (root, _) =>
            {
                order.Add($"{from}>{to}:{Directory.EnumerateDirectories(root, "data.backup-" + from + "-*").Count()}");
                return Task.CompletedTask;
            });

        var outcome = await StorageLayoutMigrator.EnsureAsync(_root, "marker", "v3", [Step("v1", "v2"), Step("v2", "v3")], _ => null, NullLogger.Instance, CancellationToken.None);

        Assert.Equal(["v1>v2:1", "v2>v3:1"], order);
        Assert.Equal(["v1 -> v2", "v2 -> v3"], outcome.Applied.Select(a => a.Split(" (")[0]).ToArray());
        Assert.Equal("v3", File.ReadAllText(Path.Join(_root, "marker")));
        Assert.Equal(2, Directory.EnumerateDirectories(_root, "data.backup-*").Count());
    }

    [Fact]
    public async Task AStepThatFailsLeavesTheMarkerWhereItWasSoItRunsAgain()
    {
        File.WriteAllText(Directory.CreateDirectory(_root).FullName + "/marker", "v1");
        var attempts = 0;
        var step = new StorageMigrationStep("v1", "v2", "flaky", [], (_, _) =>
            ++attempts == 1 ? throw new IOException("disk full") : Task.CompletedTask);

        await Assert.ThrowsAsync<IOException>(() =>
            StorageLayoutMigrator.EnsureAsync(_root, "marker", "v2", [step], _ => null, NullLogger.Instance, CancellationToken.None));
        Assert.Equal("v1", File.ReadAllText(Path.Join(_root, "marker")));

        await StorageLayoutMigrator.EnsureAsync(_root, "marker", "v2", [step], _ => null, NullLogger.Instance, CancellationToken.None);

        Assert.Equal("v2", File.ReadAllText(Path.Join(_root, "marker")));
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task AMissingStepForAKnownOldVersionIsRefused()
    {
        File.WriteAllText(Directory.CreateDirectory(_root).FullName + "/marker", "v1");

        await Assert.ThrowsAsync<StorageMigrationException>(() =>
            StorageLayoutMigrator.EnsureAsync(_root, "marker", "v3", [new("v2", "v3", "x", [], (_, _) => Task.CompletedTask)], _ => null, NullLogger.Instance, CancellationToken.None));
    }

    [Fact]
    public async Task AnUnmarkedDirectoryWithOldFilesIsReportedAndOneWithoutIsFresh()
    {
        var problem = await Assert.ThrowsAsync<StorageMigrationException>(() =>
            StorageLayoutMigrator.EnsureAsync(_root, "marker", "v1", [], _ => "old files are here", NullLogger.Instance, CancellationToken.None));
        Assert.Equal("old files are here", problem.Message);

        var fresh = await StorageLayoutMigrator.EnsureAsync(_root, "marker", "v1", [], _ => null, NullLogger.Instance, CancellationToken.None);
        Assert.True(fresh.FreshInstall);
    }

    internal static void CopyExport(string target)
    {
        var source = Path.Join(AppContext.BaseDirectory, "Fixtures", "raft-log-export");
        Assert.True(Directory.Exists(source), "the export fixture was not copied to the test output");
        CopyDirectory(source, Path.Join(target, "export"));
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
}
