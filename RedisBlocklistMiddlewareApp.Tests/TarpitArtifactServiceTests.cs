using System.IO.Compression;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class TarpitArtifactServiceTests
{
    [Fact]
    public async Task TryGetArtifactAsync_ReturnsDeterministicZip_ForSameRotationBucket()
    {
        using var harness = TarpitArtifactHarness.Create();
        var service = harness.CreateService();
        await service.PrepareAsync(CancellationToken.None);

        var first = await service.TryGetArtifactAsync("manual/archive/assets.zip", "198.51.100.101", CancellationToken.None);
        var second = await service.TryGetArtifactAsync("manual/archive/assets.zip", "198.51.100.101", CancellationToken.None);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.FileName, second!.FileName);
        Assert.Equal(first.Content, second.Content);

        using var archive = new ZipArchive(new MemoryStream(first.Content), ZipArchiveMode.Read);
        Assert.NotEmpty(archive.Entries);
        Assert.All(archive.Entries, entry => Assert.EndsWith(".js", entry.FullName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TryGetArtifactAsync_CleansUpOldArchives_WhenRotationAdvances()
    {
        using var harness = TarpitArtifactHarness.Create();
        var service = harness.CreateService(maximumArchivesToKeep: 2, archiveRotationMinutes: 1);
        await service.PrepareAsync(CancellationToken.None);

        await service.TryGetArtifactAsync("manual/archive/assets.zip", "198.51.100.102", CancellationToken.None);
        harness.AdvanceMinutes(1);
        await service.PrepareAsync(CancellationToken.None);
        await service.TryGetArtifactAsync("manual/archive/assets.zip", "198.51.100.102", CancellationToken.None);
        harness.AdvanceMinutes(1);
        await service.PrepareAsync(CancellationToken.None);
        await service.TryGetArtifactAsync("manual/archive/assets.zip", "198.51.100.102", CancellationToken.None);

        var archives = Directory.GetFiles(harness.ArchiveDirectory, "*.zip");
        Assert.Equal(2, archives.Length);
    }

    [Fact]
    public async Task TryGetArtifactAsync_MapsArbitraryPathsToFixedArchiveSet()
    {
        using var harness = TarpitArtifactHarness.Create();
        var service = harness.CreateService();
        await service.PrepareAsync(CancellationToken.None);
        var names = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < 20; index++)
        {
            var artifact = await service.TryGetArtifactAsync($"manual/archive/assets-{index}.zip", "198.51.100.103", CancellationToken.None);
            Assert.NotNull(artifact);
            names.Add(artifact.FileName);
        }

        Assert.True(names.Count <= 4, $"Expected at most four archive variants, found {names.Count}.");
    }

    [Fact]
    public async Task TryGetArtifactAsync_BoundsPerClientFetches_AndResetsAfterWindow()
    {
        using var harness = TarpitArtifactHarness.Create();
        var service = harness.CreateService();
        await service.PrepareAsync(CancellationToken.None);

        for (var index = 0; index < 30; index++)
            Assert.NotNull(await service.TryGetArtifactAsync("assets.zip", "198.51.100.104", CancellationToken.None));
        Assert.Null(await service.TryGetArtifactAsync("assets.zip", "198.51.100.104", CancellationToken.None));
        Assert.Null(await service.TryGetArtifactAsync("assets.zip", "unknown", CancellationToken.None));

        harness.AdvanceMinutes(1);
        Assert.NotNull(await service.TryGetArtifactAsync("assets.zip", "198.51.100.104", CancellationToken.None));
    }

    [Fact]
    public async Task TryGetArtifactAsync_RebuildsMissingPreparedArchive()
    {
        using var harness = TarpitArtifactHarness.Create();
        var service = harness.CreateService(maximumArchivesToKeep: 1);
        await service.PrepareAsync(CancellationToken.None);
        var first = await service.TryGetArtifactAsync("assets.zip", "198.51.100.105", CancellationToken.None);
        Assert.NotNull(first);
        File.Delete(Path.Combine(harness.ArchiveDirectory, first.FileName));

        Assert.Null(await service.TryGetArtifactAsync("assets.zip", "198.51.100.105", CancellationToken.None));
        await service.PrepareAsync(CancellationToken.None);
        Assert.NotNull(await service.TryGetArtifactAsync("assets.zip", "198.51.100.105", CancellationToken.None));
    }

    private sealed class TarpitArtifactHarness : IDisposable
    {
        private readonly string _rootPath;
        private readonly TestTimeProvider _timeProvider;

        private TarpitArtifactHarness(string rootPath, TestTimeProvider timeProvider)
        {
            _rootPath = rootPath;
            _timeProvider = timeProvider;
        }

        public string ArchiveDirectory => Path.Combine(_rootPath, "archives");

        public static TarpitArtifactHarness Create()
        {
            var rootPath = Path.Combine(Path.GetTempPath(), "ai-scraping-defense-artifact-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(rootPath);
            return new TarpitArtifactHarness(rootPath, new TestTimeProvider(DateTimeOffset.Parse("2026-03-15T12:00:00Z")));
        }

        public TarpitArtifactService CreateService(int maximumArchivesToKeep = 5, int archiveRotationMinutes = 60)
        {
            return new TarpitArtifactService(
                Options.Create(new DefenseEngineOptions
                {
                    Tarpit = new TarpitOptions
                    {
                        ArchiveDirectory = "archives",
                        MaximumArchivesToKeep = maximumArchivesToKeep,
                        ArchiveRotationMinutes = archiveRotationMinutes,
                        JavaScriptDecoyFileCount = 3,
                        MinJavaScriptDecoyFileSizeKb = 1,
                        MaxJavaScriptDecoyFileSizeKb = 1
                    }
                }),
                new TestHostEnvironment(_rootPath),
                _timeProvider,
                NullLogger<TarpitArtifactService>.Instance);
        }

        public void AdvanceMinutes(int minutes)
        {
            _timeProvider.Advance(TimeSpan.FromMinutes(minutes));
        }

        public void Dispose()
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }
        }
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public TestHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
        }

        public string EnvironmentName { get; set; } = "Development";

        public string ApplicationName { get; set; } = "RedisBlocklistMiddlewareApp.Tests";

        public string ContentRootPath { get; set; }

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TestTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public TestTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan by)
        {
            _utcNow = _utcNow.Add(by);
        }
    }
}
