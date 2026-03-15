using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class HttpPeerSignalFeedClientTests
{
    [Fact]
    public async Task FetchAsync_ReadsLegacyIpArray()
    {
        var client = CreateClient("""["198.51.100.1","198.51.100.2"]""");

        var result = await client.FetchAsync(CreatePeer(), CancellationToken.None);

        Assert.Equal(2, result.Signals.Count);
        Assert.Equal("198.51.100.1", result.Signals[0].IpAddress);
    }

    [Fact]
    public async Task FetchAsync_ReadsSignalEnvelope()
    {
        var client = CreateClient("""
        {
          "source": "peer-east",
          "signals": [
            {
              "ip_address": "198.51.100.3",
              "summary": "blocked by peer",
              "signals": ["peer_signal"],
              "observed_at_utc": "2026-03-15T00:00:00Z",
              "decided_at_utc": "2026-03-15T00:01:00Z"
            }
          ]
        }
        """);

        var result = await client.FetchAsync(CreatePeer(), CancellationToken.None);

        Assert.Equal("peer-east", result.Source);
        Assert.Single(result.Signals);
        Assert.Equal("198.51.100.3", result.Signals[0].IpAddress);
        Assert.Contains("peer_signal", result.Signals[0].Signals);
    }

    private static HttpPeerSignalFeedClient CreateClient(string body)
    {
        return new HttpPeerSignalFeedClient(
            new FakeHttpClientFactory(body),
            Options.Create(new DefenseEngineOptions
            {
                PeerSync = new PeerSyncOptions
                {
                    RequestTimeoutSeconds = 10
                }
            }),
            NullLogger<HttpPeerSignalFeedClient>.Instance);
    }

    private static PeerSyncPeerOptions CreatePeer()
    {
        return new PeerSyncPeerOptions
        {
            Name = "peer-a",
            Url = "https://peer.example.test/peer-sync/signals"
        };
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public FakeHttpClientFactory(string body)
        {
            _handler = new StaticResponseHandler(body);
        }

        public HttpClient CreateClient(string name)
        {
            return new HttpClient(_handler, disposeHandler: false);
        }
    }

    private sealed class StaticResponseHandler : HttpMessageHandler
    {
        private readonly string _body;

        public StaticResponseHandler(string body)
        {
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body, Encoding.UTF8)
            };
            response.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("application/json");
            return Task.FromResult(response);
        }
    }
}
