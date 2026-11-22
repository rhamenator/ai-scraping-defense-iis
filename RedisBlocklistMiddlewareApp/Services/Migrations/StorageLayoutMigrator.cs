using System.Globalization;

namespace RedisBlocklistMiddlewareApp.Services.Migrations;

/// <summary>One step from one on-disk layout version to the next.</summary>
/// <param name="From">The layout version this step starts from (the marker value).</param>
/// <param name="To">The layout version the step produces.</param>
/// <param name="Description">What the step does, for the log.</param>
/// <param name="BackupPaths">Paths, relative to the storage root, copied aside before the step runs.</param>
/// <param name="Apply">The change itself. It must be safe to run again if it was interrupted.</param>
internal sealed record StorageMigrationStep(
    string From,
    string To,
    string Description,
    IReadOnlyList<string> BackupPaths,
    Func<string, CancellationToken, Task> Apply);

/// <summary>Thrown when stored data cannot be used by this version and starting would risk losing it.</summary>
internal sealed class StorageMigrationException : InvalidOperationException
{
    public StorageMigrationException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Moves a directory of stored data from the layout version it was written in to the one this release uses. The pattern, which
/// the next migration of anything stored on disk should reuse:
/// <list type="number">
/// <item>A small marker file holds the layout version. It is written last, atomically (temporary file, then rename), so a
/// crash mid-migration leaves the old marker and the step simply runs again.</item>
/// <item>Before a step runs, the paths it will change are copied aside, and the copy is kept.</item>
/// <item>Steps are ordered and each is idempotent; the marker moves one step at a time.</item>
/// <item>Data this release does not recognise is refused, never guessed at: a newer marker means a newer release wrote it, and
/// data with no marker but with files that a previous layout would have written is reported with instructions.</item>
/// </list>
/// See <c>docs/migrations.md</c>.
/// </summary>
internal static class StorageLayoutMigrator
{
    /// <summary>Brings <paramref name="root"/> to <paramref name="currentVersion"/>, running whichever steps are needed.</summary>
    /// <param name="legacyProblem">For a directory with no marker: a message when it holds data from an unsupported older layout, or null when it is fresh.</param>
    public static async Task<StorageMigrationOutcome> EnsureAsync(
        string root,
        string markerFileName,
        string currentVersion,
        IReadOnlyList<StorageMigrationStep> steps,
        Func<string, string?> legacyProblem,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(root);
        var markerPath = Path.Join(root, markerFileName);
        var applied = new List<string>();

        if (!File.Exists(markerPath))
        {
            if (legacyProblem(root) is { } problem)
            {
                throw new StorageMigrationException(problem);
            }

            await WriteMarkerAsync(markerPath, currentVersion, cancellationToken);
            return new StorageMigrationOutcome(currentVersion, FreshInstall: true, applied);
        }

        var version = (await File.ReadAllTextAsync(markerPath, cancellationToken)).Trim();
        while (version != currentVersion)
        {
            var step = steps.FirstOrDefault(candidate => candidate.From == version)
                ?? throw new StorageMigrationException(
                    $"The data in {root} is marked as layout '{version}', which this release cannot use (it understands up to '{currentVersion}'). " +
                    "If it was written by a newer release, run that release; if you are downgrading, restore the backup taken before the upgrade. Nothing was changed.");

            logger.LogWarning("Migrating {Root} from layout {From} to {To}: {Description}", root, step.From, step.To, step.Description);
            var backups = BackUp(root, step, logger);
            await step.Apply(root, cancellationToken);
            await WriteMarkerAsync(markerPath, step.To, cancellationToken);
            applied.Add($"{step.From} -> {step.To}" + (backups.Count > 0 ? $" (backup: {string.Join(", ", backups)})" : string.Empty));
            version = step.To;
        }

        return new StorageMigrationOutcome(currentVersion, FreshInstall: false, applied);
    }

    private static List<string> BackUp(string root, StorageMigrationStep step, ILogger logger)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var made = new List<string>();
        foreach (var relative in step.BackupPaths)
        {
            var source = Path.Join(root, relative);
            if (!Directory.Exists(source))
            {
                continue;
            }

            var target = $"{source}.backup-{step.From}-{stamp}";
            CopyDirectory(source, target);
            made.Add(Path.GetFileName(target));
            logger.LogInformation("Backed up {Source} to {Target} before migrating; delete it once the node is healthy.", source, target);
        }

        return made;
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

    private static async Task WriteMarkerAsync(string markerPath, string version, CancellationToken cancellationToken)
    {
        var temporary = markerPath + ".tmp";
        await File.WriteAllTextAsync(temporary, version, cancellationToken);
        File.Move(temporary, markerPath, overwrite: true);
    }
}

/// <summary>What <see cref="StorageLayoutMigrator.EnsureAsync"/> did.</summary>
internal sealed record StorageMigrationOutcome(string Version, bool FreshInstall, IReadOnlyList<string> Applied);
