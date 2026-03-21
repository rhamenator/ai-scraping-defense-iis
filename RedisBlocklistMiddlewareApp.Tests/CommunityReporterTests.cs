using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class CommunityReporterTests
{
    [Fact]
    public async Task ReportAsync_PostsExpectedFormPayload()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var reporter = new CommunityReporter(
            Options.Create(CreateOptions()),
            new FakeHttpClientFactory(handler));

        var result = await reporter.ReportAsync(CreateEvent("AI scraper detected"), cancellationToken);

        Assert.NotNull(result);
        Assert.Equal(IntakeDeliveryStatuses.Succeeded, result!.Status);
        Assert.Equal(IntakeDeliveryTypes.CommunityReport, result.DeliveryType);
        Assert.Equal("AbuseIPDB", result.Channel);

        Assert.NotNull(handler.Request);
        Assert.Equal("integration-key", handler.Request!.Headers.GetValues("Key").Single());
        var body = await handler.Request.Content!.ReadAsStringAsync(cancellationToken);
        Assert.Contains("ip=198.51.100.44", body);
        Assert.Contains("categories=19", body);
        Assert.Contains("AI+Defense+Stack+detection", body);
    }

    [Fact]
    public async Task ReportAsync_UsesDefaultCategoryWhenReasonDoesNotMatchKnownPatterns()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var handler = new RecordingHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK));
        var reporter = new CommunityReporter(
            Options.Create(CreateOptions()),
            new FakeHttpClientFactory(handler));

        await reporter.ReportAsync(CreateEvent("manual review verdict"), cancellationToken);

        var body = await handler.Request!.Content!.ReadAsStringAsync(cancellationToken);
        Assert.Contains("categories=18%2C20", body);
    }

    private static DefenseEngineOptions CreateOptions()
    {
        return new DefenseEngineOptions
        {
            Intake = new IntakeOptions
            {
                CommunityReporting = new CommunityReportingOptions
                {
                    Enabled = true,
                    ProviderName = "AbuseIPDB",
                    Endpoint = "https://abuse.example.test/report",
                    ApiKeyHeaderName = "Key",
                    ApiKey = "integration-key",
                    TimeoutSeconds = 5,
                    DefaultCategories = "18,20",
                    CommentPrefix = "AI Defense Stack detection"
                }
            }
        };
    }

    private static IntakeWebhookEvent CreateEvent(string reason)
    {
        return new IntakeWebhookEvent(
            "confirmed_bot",
            reason,
            DateTimeOffset.Parse("2026-03-15T12:00:00Z"),
            new IntakeWebhookDetails(
                "198.51.100.44",
                "GET",
                "/reports/export",
                string.Empty,
                "bot-agent",
                ["signal"]));
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public FakeHttpClientFactory(HttpMessageHandler handler)
        {
            _handler = handler;
        }

        public HttpClient CreateClient(string name)
        {
            return new HttpClient(_handler, disposeHandler: false);
        }
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;

        public RecordingHttpMessageHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = CloneRequest(request);
            return Task.FromResult(_response);
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage request)
        {
            var clone = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (request.Content is not null)
            {
                var body = request.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                clone.Content = new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded");
                foreach (var header in request.Content.Headers)
                {
                    clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return clone;
        }
    }
}
