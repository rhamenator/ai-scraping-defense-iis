using System.IO.Compression;
using System.Net;
using System.Text;
using AiScrapingDefense.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class BoundedHttpResponseTests
{
    [Fact]
    public async Task ReadBoundedAsync_ReturnsABodyUnderTheLimit()
    {
        using var content = new StringContent("hello");

        await using var buffer = await content.ReadBoundedAsync(1024, TestContext.Current.CancellationToken);

        Assert.Equal("hello", Encoding.UTF8.GetString(buffer.ToArray()));
        Assert.Equal(0, buffer.Position);
    }

    [Fact]
    public async Task ReadBoundedAsync_AcceptsExactlyTheLimitAndRejectsOneByteMore()
    {
        using var exact = new ByteArrayContent(new byte[100]);
        using var over = new ByteArrayContent(new byte[101]);

        await using var ok = await exact.ReadBoundedAsync(100, TestContext.Current.CancellationToken);

        Assert.Equal(100, ok.Length);
        await Assert.ThrowsAsync<ResponseTooLargeException>(
            () => over.ReadBoundedAsync(100, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadBoundedAsync_RejectsAnOversizedDeclaredLengthWithoutReadingTheBody()
    {
        var source = new TrackingStream(endless: false);
        using var content = new StreamContent(source);
        content.Headers.ContentLength = 50_000_000;

        var error = await Assert.ThrowsAsync<ResponseTooLargeException>(
            () => content.ReadBoundedAsync(1_000_000, TestContext.Current.CancellationToken));

        Assert.Equal(50_000_000, error.DeclaredBytes);
        Assert.Equal(0, source.BytesRead);
    }

    [Fact]
    public async Task ReadBoundedAsync_StopsAnEndlessBodyThatDeclaresNoLength()
    {
        var source = new TrackingStream(endless: true);
        using var content = new StreamContent(source);

        await Assert.ThrowsAsync<ResponseTooLargeException>(
            () => content.ReadBoundedAsync(64 * 1024, TestContext.Current.CancellationToken));

        // it must stop within a chunk or two of the limit, never keep reading
        Assert.InRange(source.BytesRead, 64 * 1024, 64 * 1024 + 64 * 1024);
    }

    [Fact]
    public async Task ReadBoundedAsync_CountsDecodedBytesSoACompressedBodyThatExpandsIsStopped()
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            var zeros = new byte[1024 * 1024];
            for (var i = 0; i < 40; i++)
            {
                gzip.Write(zeros);
            }
        }

        Assert.True(compressed.Length < 200_000, "the bomb should be small on the wire");
        compressed.Position = 0;
        using var decoded = new GZipStream(compressed, CompressionMode.Decompress);
        using var content = new StreamContent(decoded);

        await Assert.ThrowsAsync<ResponseTooLargeException>(
            () => content.ReadBoundedAsync(1024 * 1024, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadBoundedStringAsync_DecodesUtf8AndAppliesTheSameLimit()
    {
        using var small = new StringContent("héllo wörld", Encoding.UTF8);
        using var big = new StringContent(new string('x', 5000));

        Assert.Equal("héllo wörld", await small.ReadBoundedStringAsync(1024, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ResponseTooLargeException>(
            () => big.ReadBoundedStringAsync(1024, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadBoundedStringAsync_HonoursTheDeclaredCharset()
    {
        var utf16 = new ByteArrayContent(Encoding.Unicode.GetBytes("198.51.100.1\n198.51.100.2\n"));
        utf16.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain") { CharSet = "utf-16" };
        var latin1 = new ByteArrayContent(Encoding.Latin1.GetBytes("caf\u00e9"));
        latin1.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain") { CharSet = "iso-8859-1" };

        Assert.Equal("198.51.100.1\n198.51.100.2\n", await utf16.ReadBoundedStringAsync(1024, TestContext.Current.CancellationToken));
        Assert.Equal("caf\u00e9", await latin1.ReadBoundedStringAsync(1024, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadBoundedStringAsync_StripsAByteOrderMarkAndFallsBackToUtf8()
    {
        var withBom = new ByteArrayContent([.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("198.51.100.3\n")]);
        var unknownCharset = new ByteArrayContent(Encoding.UTF8.GetBytes("h\u00e9llo"));
        unknownCharset.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain") { CharSet = "not-a-real-charset" };

        var bomText = await withBom.ReadBoundedStringAsync(1024, TestContext.Current.CancellationToken);

        Assert.Equal("198.51.100.3\n", bomText);
        Assert.DoesNotContain('\uFEFF', bomText);
        Assert.Equal("h\u00e9llo", await unknownCharset.ReadBoundedStringAsync(1024, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SendWithHeadersOnlyAsync_ReturnsBeforeTheBodyHasBeenRead()
    {
        var source = new TrackingStream(endless: true);
        using var handler = new ContentHandler(() => new StreamContent(source));
        using var client = new HttpClient(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://feed.example.test/list");

        using var response = await client.SendWithHeadersOnlyAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, source.BytesRead);
    }

    [Fact]
    public async Task CommunityFeedClient_RejectsAnEndlessFeedInsteadOfBufferingIt()
    {
        var source = new TrackingStream(endless: true);
        using var handler = new ContentHandler(() => new StreamContent(source));
        var client = new HttpCommunityBlocklistFeedClient(
            new SingleClientFactory(handler),
            Options.Create(new DefenseEngineOptions { CommunityBlocklist = new CommunityBlocklistOptions { RequestTimeoutSeconds = 30 } }),
            NullLogger<HttpCommunityBlocklistFeedClient>.Instance);

        await Assert.ThrowsAsync<ResponseTooLargeException>(() => client.FetchAsync(
            new CommunityBlocklistSourceOptions { Name = "feed", Url = "https://feed.example.test/list" },
            TestContext.Current.CancellationToken));

        Assert.InRange(source.BytesRead, 0, HttpCommunityBlocklistFeedClient.MaxResponseBytes + 64 * 1024);
    }

    [Fact]
    public async Task PeerFeedClient_RejectsAnOversizedDeclaredLengthBeforeParsing()
    {
        var source = new TrackingStream(endless: false);
        using var handler = new ContentHandler(() =>
        {
            var content = new StreamContent(source);
            content.Headers.ContentLength = HttpPeerSignalFeedClient.MaxResponseBytes + 1;
            return content;
        });
        var client = new HttpPeerSignalFeedClient(
            new SingleClientFactory(handler),
            Options.Create(new DefenseEngineOptions { PeerSync = new PeerSyncOptions { RequestTimeoutSeconds = 30 } }),
            NullLogger<HttpPeerSignalFeedClient>.Instance);

        await Assert.ThrowsAsync<ResponseTooLargeException>(() => client.FetchAsync(
            new PeerSyncPeerOptions { Name = "peer", Url = "https://peer.example.test/signals" },
            TestContext.Current.CancellationToken));

        Assert.Equal(0, source.BytesRead);
    }

    private sealed class TrackingStream(bool endless) : Stream
    {
        public long BytesRead { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            if (!endless)
            {
                return 0;
            }

            buffer.Clear();
            BytesRead += buffer.Length;
            return buffer.Length;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read(buffer.Span));

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ContentHandler(Func<HttpContent> content) : HttpMessageHandler
    {
        private readonly List<HttpResponseMessage> _responses = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content() };
            _responses.Add(response);
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _responses.ForEach(response => response.Dispose());
            }

            base.Dispose(disposing);
        }
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
