using System.IO.Compression;
using System.Net;

namespace AiScrapingDefense.IntegrationTests;

[Collection(EndToEndCollection.Name)]
public sealed class TarpitArtifactFlowTests
{
    private readonly DefenseStackFixture _fixture;

    public TarpitArtifactFlowTests(DefenseStackFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task TarpitArchiveRoute_ReturnsZipDecoyArtifact()
    {
        await using var host = await _fixture.CreateHostAsync();
        var response = await host.Client.GetAsync("/anti-scrape-tarpit/reference/manual/archive/assets.zip");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        var bytes = await response.Content.ReadAsByteArrayAsync();

        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        Assert.NotEmpty(archive.Entries);
        Assert.All(archive.Entries, entry => Assert.EndsWith(".js", entry.FullName, StringComparison.OrdinalIgnoreCase));
    }
}
