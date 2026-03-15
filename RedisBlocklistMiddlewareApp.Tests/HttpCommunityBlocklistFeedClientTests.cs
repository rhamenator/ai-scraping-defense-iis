using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class HttpCommunityBlocklistFeedClientTests
{
    [Fact]
    public async Task FetchAsync_ReadsJsonArrayFeeds()
    {
        var client = CreateClient("""
        ["198.51.100.1","198.51.100.2"]
        """, "application/json");

        var result = await client.FetchAsync(CreateSource(), CancellationToken.None);

        Assert.Equal(["198.51.100.1", "198.51.100.2"], result);
    }

    [Fact]
    public async Task FetchAsync_ReadsJsonObjectFeeds()
    {
        var client = CreateClient("""
        {"ips":["198.51.100.3","198.51.100.4"]}
        """, "application/json");

        var result = await client.FetchAsync(CreateSource(), CancellationToken.None);

        Assert.Equal(["198.51.100.3", "198.51.100.4"], result);
    }

    [Fact]
    public async Task FetchAsync_ReadsPlainTextFeeds()
    {
        var client = CreateClient("198.51.100.5\n198.51.100.6\n", "text/plain");

        var result = await client.FetchAsync(CreateSource(), CancellationToken.None);

        Assert.Equal(["198.51.100.5", "198.51.100.6"], result);
    }

    [Fact]
    public async Task FetchAsync_StripsPlainTextCommentsAndWhitespace()
    {
        var client = CreateClient("# comment\r\n 198.51.100.7 \r\n\r\n# skip\r\n198.51.100.8\r\n", "text/plain; charset=utf-8");

        var result = await client.FetchAsync(CreateSource(), CancellationToken.None);

        Assert.Equal(["198.51.100.7", "198.51.100.8"], result);
    }

    private static HttpCommunityBlocklistFeedClient CreateClient(string body, string contentType)
    {
        return new HttpCommunityBlocklistFeedClient(
            new FakeHttpClientFactory(body, contentType),
            Options.Create(new DefenseEngineOptions
            {
                CommunityBlocklist = new CommunityBlocklistOptions
                {
                    RequestTimeoutSeconds = 10
                }
            }),
            NullLogger<HttpCommunityBlocklistFeedClient>.Instance);
    }

    private static CommunityBlocklistSourceOptions CreateSource()
    {
        return new CommunityBlocklistSourceOptions
        {
            Name = "test-feed",
            Url = "https://community.example.test/list"
        };
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public FakeHttpClientFactory(string body, string contentType)
        {
            _handler = new StaticResponseHandler(body, contentType);
        }

        public HttpClient CreateClient(string name)
        {
            return new HttpClient(_handler, disposeHandler: false);
        }
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly string _body;
        private readonly string _contentType;

        public StaticResponseHandler(string body, string contentType)
        {
            _body = body;
            _contentType = contentType;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8)
            };
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(_contentType);
            return Task.FromResult(response);
        }
    }
}
