using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using RedisBlocklistMiddlewareApp.Services.Migrations;

namespace RedisBlocklistMiddlewareApp.Services;

/// <summary>
/// The on-disk layout of a consensus node's storage, and how an older release's data is brought forward.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>log</c> (releases up to 1.2, DotNext 5.x): readable only by DotNext 5.x. Release 1.2 also writes an export of the
/// committed entries (<c>export/raft-log-v1</c>); this release imports that export into a new write-ahead log and archives <c>log</c>.</item>
/// <item><c>wal-1</c> (this release, DotNext 6): a <c>WriteAheadLog</c> in <c>wal</c> plus snapshots in <c>snapshots</c>. Marker file
/// <c>layout.version</c> is written last, so a node interrupted during import simply imports again.</item>
/// </list>
/// Later layouts add a <see cref="StorageMigrationStep"/> to <see cref="Steps"/>; see docs/migrations.md.
/// </remarks>
internal static class RaftLogLayout
{
    public const string LegacyDirectoryName = "log";
    public const string WalDirectoryName = "wal";
    public const string SnapshotDirectoryName = "snapshots";
    public const string MarkerFileName = "layout.version";
    public const string CurrentVersion = "wal-1";

    internal static readonly IReadOnlyList<StorageMigrationStep> Steps = [];

    /// <summary>Returns the WAL and snapshot directories, importing or migrating older data first when it is found.</summary>
    public static async Task<(string WalPath, string SnapshotPath)> PrepareAsync(
        string storagePath,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(storagePath);
        if (!File.Exists(Path.Join(storagePath, MarkerFileName)) && HasLegacyLog(storagePath))
        {
            await RaftLogImport.ImportAsync(storagePath, logger, cancellationToken);
        }

        await StorageLayoutMigrator.EnsureAsync(
            storagePath,
            MarkerFileName,
            CurrentVersion,
            Steps,
            legacyProblem: _ => null,
            logger,
            cancellationToken);

        var wal = Path.Join(storagePath, WalDirectoryName);
        var snapshots = Path.Join(storagePath, SnapshotDirectoryName);
        Directory.CreateDirectory(wal);
        Directory.CreateDirectory(snapshots);
        return (wal, snapshots);
    }

    internal static bool HasLegacyLog(string storagePath)
    {
        var legacy = Path.Join(storagePath, LegacyDirectoryName);
        return Directory.Exists(legacy) && Directory.EnumerateFileSystemEntries(legacy).Any();
    }
}
