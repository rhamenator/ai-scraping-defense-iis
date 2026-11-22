using System.Text.Json;
using DotNext.IO.Log;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using RedisBlocklistMiddlewareApp.Services.Migrations;

namespace RedisBlocklistMiddlewareApp.Services;

/// <summary>
/// Imports the export written by release 1.2 (<c>export/raft-log-v1</c>) into a new DotNext 6 write-ahead log.
/// </summary>
/// <remarks>
/// Order matters and is what makes an interruption harmless: the new log is built beside the real location, verified by
/// reopening it, moved into place, and only then is the version marker written. The old <c>log</c> is archived last. Until the
/// marker exists the node treats the directory as not yet migrated and starts the import over.
/// </remarks>
internal static class RaftLogImport
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task ImportAsync(string storagePath, ILogger logger, CancellationToken cancellationToken)
    {
        var exportPath = Path.Join(storagePath, "export", "raft-log-v1");
        var entriesPath = Path.Join(exportPath, "entries.jsonl");
        var metaPath = Path.Join(exportPath, "meta.json");
        if (!File.Exists(entriesPath) || !File.Exists(metaPath))
        {
            throw new StorageMigrationException(
                $"{storagePath} holds a consensus log from DotNext 5 (the 'log' directory) but no export of it. This release cannot read that log. " +
                "Start release 1.2 once against this directory and stop it normally; it writes the export (export/raft-log-v1). Then start this release. Nothing was changed. See docs/migrations.md.");
        }

        var meta = JsonSerializer.Deserialize<ExportMeta>(await File.ReadAllTextAsync(metaPath, cancellationToken), Json)
            ?? throw new StorageMigrationException($"{metaPath} is empty.");
        if (meta.Format != "asd-raft-log-export-v1")
        {
            throw new StorageMigrationException($"{metaPath} has an unknown export format '{meta.Format}'; this release reads asd-raft-log-export-v1. Nothing was changed.");
        }

        var actualHash = await HashAsync(entriesPath, cancellationToken);
        if (!string.Equals(actualHash, meta.EntriesSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new StorageMigrationException(
                $"{entriesPath} does not match its recorded SHA-256, so the export is incomplete or damaged. Re-run release 1.2 and stop it normally to regenerate it. Nothing was changed.");
        }

        var building = Path.Join(storagePath, "wal.importing");
        if (Directory.Exists(building))
        {
            Directory.Delete(building, recursive: true);
        }

        Directory.CreateDirectory(building);
        var imported = await WriteAsync(building, entriesPath, meta, cancellationToken);
        await VerifyAsync(building, meta, cancellationToken);

        var wal = Path.Join(storagePath, RaftLogLayout.WalDirectoryName);
        if (Directory.Exists(wal))
        {
            // A previous import was interrupted before its marker was written; what is there is not trusted.
            Directory.Delete(wal, recursive: true);
        }

        Directory.Move(building, wal);
        await WriteMarkerAsync(storagePath, cancellationToken);
        var archive = Path.Join(storagePath, $"log.imported-{DateTime.UtcNow:yyyyMMddHHmmss}");
        Directory.Move(Path.Join(storagePath, RaftLogLayout.LegacyDirectoryName), archive);
        logger.LogInformation(
            "Imported {Count} committed Raft log entries (term {Term}) from the release 1.2 export; the old log is kept at {Archive}.",
            imported,
            meta.Term,
            archive);
    }

    private static async Task<long> WriteAsync(string directory, string entriesPath, ExportMeta meta, CancellationToken cancellationToken)
    {
        await using var wal = new WriteAheadLog(new WriteAheadLog.Options { Location = directory }, new DiscardingStateMachine());
        await wal.InitializeAsync(cancellationToken);
        long count = 0;
        await foreach (var line in ReadLinesAsync(entriesPath, cancellationToken))
        {
            var entry = JsonSerializer.Deserialize<ExportedEntry>(line, Json)
                ?? throw new StorageMigrationException("The export contains an empty line.");
            if (entry.I != count + 1)
            {
                throw new StorageMigrationException($"The export is not contiguous: expected entry {count + 1}, found {entry.I}. Nothing was changed.");
            }

            await wal.AppendAsync(new BinaryLogEntry { Content = Convert.FromBase64String(entry.P), Term = entry.T }, entry.I, cancellationToken);
            count++;
        }

        if (count != meta.Count)
        {
            throw new StorageMigrationException($"The export lists {meta.Count} entries but contains {count}. Nothing was changed.");
        }

        if (count > 0)
        {
            await wal.CommitAsync(count, cancellationToken);
            await wal.WaitForApplyAsync(count, cancellationToken);
        }

        await ((IPersistentState)wal).UpdateTermAsync(meta.Term, resetLastVote: false);
        await wal.FlushAsync(cancellationToken);
        return count;
    }

    /// <summary>Reopens the finished log and checks it holds what the export said, so a bad import is caught before anything is replaced.</summary>
    private static async Task VerifyAsync(string directory, ExportMeta meta, CancellationToken cancellationToken)
    {
        await using var wal = new WriteAheadLog(new WriteAheadLog.Options { Location = directory }, new DiscardingStateMachine());
        await wal.InitializeAsync(cancellationToken);
        if (wal.LastEntryIndex != meta.Count || wal.LastCommittedEntryIndex != meta.Count || ((IPersistentState)wal).Term != meta.Term)
        {
            throw new StorageMigrationException(
                $"The imported log does not match the export (last {wal.LastEntryIndex}, committed {wal.LastCommittedEntryIndex}, term {((IPersistentState)wal).Term}; expected {meta.Count}/{meta.Count}/{meta.Term}). Nothing was changed.");
        }
    }

    private static async Task WriteMarkerAsync(string storagePath, CancellationToken cancellationToken)
    {
        var marker = Path.Join(storagePath, RaftLogLayout.MarkerFileName);
        var temporary = marker + ".tmp";
        await File.WriteAllTextAsync(temporary, RaftLogLayout.CurrentVersion, cancellationToken);
        File.Move(temporary, marker, overwrite: true);
    }

    private static async IAsyncEnumerable<string> ReadLinesAsync(string path, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(path);
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (line.Length > 0)
            {
                yield return line;
            }
        }
    }

    private static async Task<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private sealed record ExportMeta(string Format, long Count, long CommittedIndex, long LastIndex, long Term, string EntriesSha256);

    private sealed record ExportedEntry(long I, long T, string P);

    /// <summary>Accepts entries during import without touching the blocklist; the real state machine replays them on first start.</summary>
    private sealed class DiscardingStateMachine : IStateMachine
    {
        public ISnapshot? Snapshot => null;

        public ValueTask ReclaimGarbageAsync(long watermark, CancellationToken token) => ValueTask.CompletedTask;

        public ValueTask<long> ApplyAsync(LogEntry entry, CancellationToken token) => ValueTask.FromResult(entry.Index);
    }
}
