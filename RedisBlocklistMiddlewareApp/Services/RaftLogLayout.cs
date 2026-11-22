using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using RedisBlocklistMiddlewareApp.Services.Migrations;

namespace RedisBlocklistMiddlewareApp.Services;

/// <summary>
/// The on-disk layout of a consensus node's storage, and how older layouts are brought forward.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>log</c> (releases before 1.2): a DotNext 5.x <c>DiskBasedStateMachine</c> directory. It can only be read by DotNext 5.x,
/// so it is converted by the 1.2 release (stage 1) and this release refuses to start on it.</item>
/// <item><c>wal-1</c> (release 1.2): a <c>WriteAheadLog</c> in <c>wal</c> with no snapshots; the whole log is kept.</item>
/// <item><c>wal-2</c> (this release): the same <c>wal</c> directory plus snapshots in <c>snapshots</c>, so the log can be compacted.</item>
/// </list>
/// </remarks>
internal static class RaftLogLayout
{
    public const string LegacyDirectoryName = "log";
    public const string WalDirectoryName = "wal";
    public const string SnapshotDirectoryName = "snapshots";
    public const string MarkerFileName = "layout.version";
    public const string CurrentVersion = "wal-2";

    internal static readonly IReadOnlyList<StorageMigrationStep> Steps =
    [
        new StorageMigrationStep(
            From: "wal-1",
            To: "wal-2",
            Description: "add snapshot storage; the log is replayed once into the snapshot-capable state machine and then compacted",
            BackupPaths: [WalDirectoryName],
            Apply: (root, _) =>
            {
                Directory.CreateDirectory(Path.Join(root, SnapshotDirectoryName));
                return Task.CompletedTask;
            })
    ];

    /// <summary>Returns the WAL and snapshot directories, migrating an older layout first when one is found.</summary>
    public static async Task<(string WalPath, string SnapshotPath)> PrepareAsync(
        string storagePath,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        await StorageLayoutMigrator.EnsureAsync(
            storagePath,
            MarkerFileName,
            CurrentVersion,
            Steps,
            LegacyProblem,
            logger,
            cancellationToken);

        var wal = Path.Join(storagePath, WalDirectoryName);
        var snapshots = Path.Join(storagePath, SnapshotDirectoryName);
        Directory.CreateDirectory(wal);
        Directory.CreateDirectory(snapshots);
        return (wal, snapshots);
    }

    internal static string? LegacyProblem(string storagePath)
    {
        var legacy = Path.Join(storagePath, LegacyDirectoryName);
        if (!Directory.Exists(legacy) || !Directory.EnumerateFileSystemEntries(legacy).Any())
        {
            return null;
        }

        return $"{storagePath} holds a consensus log from a release before 1.2 (the '{LegacyDirectoryName}' directory) that has not been converted. " +
            "That format can only be read by release 1.2: start release 1.2 once against this directory (it converts the log and keeps the old one as " +
            "log.legacy-<timestamp>), stop it, then start this release. Nothing was changed. See docs/migrations.md.";
    }
}
