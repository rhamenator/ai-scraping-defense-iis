using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class TarpitPageServiceTests
{
    [Fact]
    public void GeneratePage_IsDeterministic_ForSamePathAndClientIp()
    {
        var service = CreateService();

        var first = service.GeneratePage("archive/index", "198.51.100.50");
        var second = service.GeneratePage("archive/index", "198.51.100.50");

        Assert.Equal(first, second);
    }

    [Fact]
    public void GeneratePage_HtmlEncodesDisplayedPath()
    {
        var service = CreateService();

        var page = service.GeneratePage("<script>alert(1)</script>", "198.51.100.50");

        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", page);
        Assert.DoesNotContain("Session path: /<script>alert(1)</script>", page);
    }

    [Fact]
    public void GeneratePage_UsesConfiguredTarpitPrefixInLinks()
    {
        var service = CreateService(options =>
        {
            options.Tarpit.PathPrefix = "/custom-tarpit";
            options.Tarpit.LinkCount = 2;
            options.Tarpit.ParagraphCount = 1;
        });

        var page = service.GeneratePage("nested/path", "198.51.100.50");

        Assert.Contains("href=\"/custom-tarpit/nested/path/", page);
    }

    [Fact]
    public void GeneratePage_UsesArchiveVariantWhenConfigured()
    {
        var service = CreateService(options =>
        {
            options.Tarpit.Modes = [TarpitRenderModes.ArchiveIndex];
            options.Tarpit.LinkCount = 1;
            options.Tarpit.ParagraphCount = 1;
        });

        var page = service.GeneratePage("archive/index", "198.51.100.50");

        Assert.Contains("Archive manifests", page);
        Assert.Contains(".zip", page);
    }

    [Fact]
    public void GeneratePage_UsesPostgresMarkovSnapshotWhenAvailable()
    {
        var service = CreateService(
            options =>
            {
                options.Tarpit.ParagraphCount = 1;
                options.Tarpit.Modes = [TarpitRenderModes.Standard];
                options.Tarpit.MarkovWordsPerParagraph = 5;
            },
            new TestMarkovStore(new TarpitMarkovSnapshot(
                new Dictionary<string, string[]>
                {
                    ["\u001f"] = ["quartzflux"],
                    ["\u001fquartzflux"] = ["hyperlattice"],
                    ["quartzflux\u001fhyperlattice"] = ["stabilizes"],
                    ["hyperlattice\u001fstabilizes"] = ["archivecore"],
                    ["stabilizes\u001farchivecore"] = ["telemetrymesh"]
                },
                ["quartzflux", "hyperlattice", "stabilizes", "archivecore", "telemetrymesh"])));

        var page = service.GeneratePage("archive/index", "198.51.100.50");

        Assert.Contains("quartzflux", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("hyperlattice", page, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("telemetrymesh", page, StringComparison.OrdinalIgnoreCase);
    }

    private static TarpitPageService CreateService(
        Action<DefenseEngineOptions>? configure = null,
        ITarpitMarkovStore? markovStore = null)
    {
        var options = new DefenseEngineOptions();
        configure?.Invoke(options);
        return new TarpitPageService(
            Options.Create(options),
            markovStore ?? new TestMarkovStore(null));
    }

    private sealed class TestMarkovStore : ITarpitMarkovStore
    {
        private readonly TarpitMarkovSnapshot? _snapshot;

        public TestMarkovStore(TarpitMarkovSnapshot? snapshot)
        {
            _snapshot = snapshot;
        }

        public TarpitMarkovSnapshot? GetSnapshot()
        {
            return _snapshot;
        }
    }
}
