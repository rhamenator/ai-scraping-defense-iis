using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class TarpitArtifactService : ITarpitArtifactService
{
    private const int MaximumVariants = 4;
    private const int MaximumClientRequestsPerMinute = 30;
    private const int MaximumGlobalRequestsPerMinute = 1000;
    private const int MaximumTrackedClients = 4096;
    private const long MaximumArchiveBytes = 4L * 1024 * 1024;
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
    private readonly ILogger<TarpitArtifactService> _logger;
    private readonly SemaphoreSlim _generationGate = new(1);
    private readonly SemaphoreSlim _readGate = new(8);
    private readonly object _rateGate = new();
    private readonly Dictionary<string, int> _clientRequests = new(StringComparer.Ordinal);
    private long _requestWindow = -1;
    private int _globalRequests;
    private long _readyBucket = -1;
    private Task _pendingGeneration = Task.CompletedTask;
    private CancellationToken _shutdownToken;

    public TarpitArtifactService(
        IOptions<DefenseEngineOptions> options,
        IHostEnvironment environment,
        TimeProvider timeProvider,
        ILogger<TarpitArtifactService> logger)
    {
        _options = options.Value.Tarpit;
        _environment = environment;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task PrepareAsync(CancellationToken shutdownToken)
    {
        _shutdownToken = shutdownToken;
        try { await WarmBucketAsync(shutdownToken); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Tarpit archive preparation failed; serving HTML fallback");
        }
    }

    public async Task<TarpitArtifact?> TryGetArtifactAsync(
        string path, string clientIp, CancellationToken cancellationToken)
    {
        if (!path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) return null;
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(clientIp) || clientIp == "unknown") return null;

        var bucket = CurrentBucket();
        if (Interlocked.Read(ref _readyBucket) != bucket)
        {
            QueueWarmBucket();
            return null;
        }
        if (!AdmitClient(clientIp)) return null;
        if (!await _readGate.WaitAsync(0, cancellationToken)) return null;

        try
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(path));
            var variant = (int)(BitConverter.ToUInt32(hash, 0) % (uint)VariantCount);
            var archiveFileName = ArchiveFileName(bucket, variant);
            var archivePath = Path.Join(ResolveArchiveDirectory(), archiveFileName);
            if (!File.Exists(archivePath))
            {
                Interlocked.Exchange(ref _readyBucket, -1);
                QueueWarmBucket();
                return null;
            }
            if (new FileInfo(archivePath).Length > MaximumArchiveBytes)
            {
                _logger.LogWarning("Tarpit archive exceeds the read limit; serving HTML fallback");
                return null;
            }

            return new TarpitArtifact(
                archiveFileName,
                "application/zip",
                await File.ReadAllBytesAsync(archivePath, cancellationToken));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning("Tarpit archive read failed; serving HTML fallback");
            return null;
        }
        finally
        {
            _readGate.Release();
        }
    }

    private int VariantCount => Math.Min(MaximumVariants, Math.Max(1, _options.MaximumArchivesToKeep));

    private long CurrentBucket() => _timeProvider.GetUtcNow().ToUnixTimeSeconds() /
        Math.Max(60L, (long)_options.ArchiveRotationMinutes * 60L);

    private static string ArchiveFileName(long bucket, int variant) => $"decoy_{variant}_{bucket}.zip";

    private bool AdmitClient(string clientIp)
    {
        var window = _timeProvider.GetUtcNow().ToUnixTimeSeconds() / 60;
        lock (_rateGate)
        {
            if (_requestWindow != window)
            {
                _requestWindow = window;
                _globalRequests = 0;
                _clientRequests.Clear();
            }
            if (_globalRequests >= MaximumGlobalRequestsPerMinute) return false;
            if (_clientRequests.TryGetValue(clientIp, out var count))
            {
                if (count >= MaximumClientRequestsPerMinute) return false;
                _clientRequests[clientIp] = count + 1;
            }
            else
            {
                if (_clientRequests.Count >= MaximumTrackedClients) return false;
                _clientRequests.Add(clientIp, 1);
            }
            _globalRequests++;
            return true;
        }
    }

    private void QueueWarmBucket()
    {
        lock (_rateGate)
        {
            if (!_pendingGeneration.IsCompleted) return;
            _pendingGeneration = Task.Run(async () =>
            {
                try { await WarmBucketAsync(_shutdownToken); }
                catch (OperationCanceledException) { _logger.LogDebug("Tarpit archive warm-up canceled"); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning("Tarpit archive warm-up failed; serving HTML fallback");
                }
            });
        }
    }

    private async Task WarmBucketAsync(CancellationToken cancellationToken)
    {
        await _generationGate.WaitAsync(cancellationToken);
        try
        {
            var bucket = CurrentBucket();
            if (Interlocked.Read(ref _readyBucket) == bucket) return;
            var archiveDirectory = ResolveArchiveDirectory();
            Directory.CreateDirectory(archiveDirectory);
            for (var variant = 0; variant < VariantCount; variant++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var archivePath = Path.Join(archiveDirectory, ArchiveFileName(bucket, variant));
                if (File.Exists(archivePath)) continue;
                var content = await Task.Run(
                    () => BuildArchive($"decoy_{variant}", bucket, cancellationToken), cancellationToken);
                var temporaryPath = $"{archivePath}.{Guid.NewGuid():N}.tmp";
                try
                {
                    await File.WriteAllBytesAsync(temporaryPath, content, cancellationToken);
                    File.Move(temporaryPath, archivePath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
                }
            }
            CleanupOldArchives(archiveDirectory, bucket);
            Interlocked.Exchange(ref _readyBucket, bucket);
        }
        finally
        {
            _generationGate.Release();
        }
    }

    private byte[] BuildArchive(string slug, long bucket, CancellationToken cancellationToken)
    {
        var random = new Random(GetSeed(slug, bucket));
        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (var index = 0; index < Math.Clamp(_options.JavaScriptDecoyFileCount, 1, 32); index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry = archive.CreateEntry(BuildEntryName(random), CompressionLevel.SmallestSize);
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8, leaveOpen: false);
                writer.Write(BuildJavaScriptPayload(slug, bucket, random, index, cancellationToken));
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

    private string BuildJavaScriptPayload(
        string slug, long bucket, Random random, int index, CancellationToken cancellationToken)
    {
        var minimumKb = Math.Clamp(_options.MinJavaScriptDecoyFileSizeKb, 1, 64);
        var maximumKb = Math.Clamp(_options.MaxJavaScriptDecoyFileSizeKb, minimumKb, 64);
        var targetBytes = random.Next(
            minimumKb * 1024,
            maximumKb * 1024 + 1);
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

        // The generated payload is ASCII, so Length is also its UTF-8 byte count.
        while (builder.Length < targetBytes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Append(RandomAscii(random, 96));
        }

        builder.AppendLine("*/");
        return builder.ToString();
    }

    private void CleanupOldArchives(string archiveDirectory, long currentBucket)
    {
        var keep = new HashSet<string>(StringComparer.Ordinal);
        var maximum = Math.Clamp(_options.MaximumArchivesToKeep, 1, 64);
        for (var offset = 0L; keep.Count < maximum && offset <= maximum; offset++)
        {
            for (var variant = 0; variant < VariantCount && keep.Count < maximum; variant++)
                keep.Add(ArchiveFileName(currentBucket - offset, variant));
        }

        foreach (var file in Directory.EnumerateFiles(archiveDirectory, "*.zip")
                     .Where(file => !keep.Contains(Path.GetFileName(file))))
        {
            File.Delete(file);
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
