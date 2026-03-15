using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class TarpitArtifactService : ITarpitArtifactService
{
    private static readonly string[] FilePrefixes =
    [
        "analytics_bundle",
        "vendor_lib",
        "core_framework",
        "ui_component_pack",
        "runtime_utils",
        "shared_modules",
        "feature_flags_data",
        "auth_client_sdk"
    ];

    private static readonly string[] FileSuffixes =
    [
        "_min",
        "_pack",
        "_bundle",
        "_core",
        string.Empty
    ];

    private readonly TarpitOptions _options;
    private readonly IHostEnvironment _environment;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate = new();

    public TarpitArtifactService(
        IOptions<DefenseEngineOptions> options,
        IHostEnvironment environment,
        TimeProvider timeProvider)
    {
        _options = options.Value.Tarpit;
        _environment = environment;
        _timeProvider = timeProvider;
    }

    public Task<TarpitArtifact?> TryGetArtifactAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<TarpitArtifact?>(null);
        }

        var archiveDirectory = ResolveArchiveDirectory();
        Directory.CreateDirectory(archiveDirectory);

        var slug = SanitizeSlug(Path.GetFileNameWithoutExtension(path));
        var bucket = _timeProvider.GetUtcNow().ToUnixTimeSeconds() / Math.Max(60, _options.ArchiveRotationMinutes * 60);
        var archiveFileName = $"{slug}_{bucket}.zip";
        var archivePath = Path.Combine(archiveDirectory, archiveFileName);
        byte[] content;

        lock (_gate)
        {
            if (!File.Exists(archivePath))
            {
                content = BuildArchive(slug, bucket);
                File.WriteAllBytes(archivePath, content);
                CleanupOldArchives(archiveDirectory);
            }
            else
            {
                content = File.ReadAllBytes(archivePath);
            }
        }

        return Task.FromResult<TarpitArtifact?>(new TarpitArtifact(
            archiveFileName,
            "application/zip",
            content));
    }

    private byte[] BuildArchive(string slug, long bucket)
    {
        var random = new Random(GetSeed(slug, bucket));
        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var index = 0; index < Math.Max(1, _options.JavaScriptDecoyFileCount); index++)
            {
                var entry = archive.CreateEntry(BuildEntryName(random), CompressionLevel.SmallestSize);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8, leaveOpen: false);
                writer.Write(BuildJavaScriptPayload(slug, bucket, random, index));
            }
        }

        return memoryStream.ToArray();
    }

    private string BuildEntryName(Random random)
    {
        var prefix = FilePrefixes[random.Next(FilePrefixes.Length)];
        var suffix = FileSuffixes[random.Next(FileSuffixes.Length)];
        var hash = random.NextInt64().ToString("x8");
        return $"{prefix}{suffix}.{hash}.js";
    }

    private string BuildJavaScriptPayload(string slug, long bucket, Random random, int index)
    {
        var targetBytes = random.Next(
            Math.Max(1, _options.MinJavaScriptDecoyFileSizeKb) * 1024,
            Math.Max(Math.Max(1, _options.MinJavaScriptDecoyFileSizeKb) * 1024 + 1, _options.MaxJavaScriptDecoyFileSizeKb * 1024));
        var builder = new StringBuilder();
        builder.AppendLine($"// synthetic decoy archive {slug}");
        builder.AppendLine($"// rotation bucket {bucket}");
        builder.AppendLine($"// entry {index}");
        builder.AppendLine("(function(){");

        for (var i = 0; i < 12; i++)
        {
            builder.Append("  const ");
            builder.Append(RandomIdentifier(random));
            builder.Append(" = ");
            builder.Append('"');
            builder.Append(RandomAscii(random, 24));
            builder.AppendLine("\";");
        }

        builder.Append("  function ");
        builder.Append(RandomIdentifier(random));
        builder.AppendLine("(){ return true; }");
        builder.AppendLine("})();");
        builder.Append("/*");

        while (Encoding.UTF8.GetByteCount(builder.ToString()) < targetBytes)
        {
            builder.Append(RandomAscii(random, 96));
        }

        builder.AppendLine("*/");
        return builder.ToString();
    }

    private void CleanupOldArchives(string archiveDirectory)
    {
        var archives = new DirectoryInfo(archiveDirectory)
            .GetFiles("*.zip")
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ToArray();

        foreach (var archive in archives.Skip(Math.Max(1, _options.MaximumArchivesToKeep)))
        {
            archive.Delete();
        }
    }

    private string ResolveArchiveDirectory()
    {
        return Path.IsPathRooted(_options.ArchiveDirectory)
            ? _options.ArchiveDirectory
            : Path.Combine(_environment.ContentRootPath, _options.ArchiveDirectory);
    }

    private int GetSeed(string slug, long bucket)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{_options.Seed}|{slug}|{bucket}"));
        return BitConverter.ToInt32(bytes, 0);
    }

    private static string SanitizeSlug(string slug)
    {
        var cleaned = new string(slug
            .Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_')
            .ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "assets" : cleaned;
    }

    private static string RandomIdentifier(Random random)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyz";
        return string.Concat(Enumerable.Range(0, 10).Select(_ => alphabet[random.Next(alphabet.Length)]));
    }

    private static string RandomAscii(Random random, int length)
    {
        const string alphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_ ;:,.-";
        return string.Concat(Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]));
    }
}
