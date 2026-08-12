using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using RedisBlocklistMiddlewareApp.Services;

namespace AiScrapingDefense.IntegrationTests;

[Collection(EndToEndCollection.Name)]
public sealed class SplitTopologyFlowTests
{
    private const string ServiceToken = "integration-split-service-token-value";
    private readonly DefenseStackFixture _fixture;

    public SplitTopologyFlowTests(DefenseStackFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SplitTopology_UsesRemoteEscalation_AndDedicatedTarpitRuntime()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        using var escalationFactory = new WebApplicationFactory<EscalationEngineAssemblyMarker>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["DefenseEngine:Topology:ServiceToken"] = ServiceToken,
                        ["DefenseEngine:Redis:ConnectionString"] = _fixture.RedisConnectionString,
                        ["DefenseEngine:Redis:FrequencyDatabase"] = "12",
                        ["DefenseEngine:Escalation:Containment:BlockScoreThreshold"] = "80"
                    }));
            });
        using var escalationClient = escalationFactory.CreateClient();
        using var escalationHealth = await escalationClient.GetAsync("/health", cancellationToken);
        Assert.Equal(HttpStatusCode.OK, escalationHealth.StatusCode);

        using var tarpitFactory = new WebApplicationFactory<TarpitApiAssemblyMarker>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                builder.ConfigureAppConfiguration((_, configuration) =>
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["DefenseEngine:Tarpit:ResponseDelayMilliseconds"] = "0",
                        ["DefenseEngine:Tarpit:PostgresMarkov:Enabled"] = "true",
                        ["DefenseEngine:Tarpit:PostgresMarkov:ConnectionString"] = _fixture.PostgresConnectionString
                    }));
            });
        using var tarpitClient = tarpitFactory.CreateClient();

        var recordingHandler = new RecordingHandler(escalationFactory.Server.CreateHandler());
        await using var edgeHost = await _fixture.CreateHostAsync(
            new Dictionary<string, string?>
            {
                ["DefenseEngine:Topology:Mode"] = "Split",
                ["DefenseEngine:Topology:EscalationBaseUrl"] = "http://escalation.test",
                ["DefenseEngine:Topology:TarpitPublicBaseUrl"] = "http://tarpit.test",
                ["DefenseEngine:Topology:ServiceToken"] = ServiceToken
            },
            services =>
            {
                services.RemoveAll<IThreatAssessmentService>();
                services.AddHttpClient<IThreatAssessmentService, RemoteThreatAssessmentService>()
                    .ConfigurePrimaryHttpMessageHandler(() => recordingHandler);
            });

        using (var request = new HttpRequestMessage(HttpMethod.Get, "/docs"))
        {
            request.Headers.Add(HeaderDrivenClientIpResolver.HeaderName, "198.51.100.91");
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0");
            request.Headers.Accept.ParseAdd("*/*");
            await edgeHost.Client.SendAsync(request, cancellationToken);
        }

        await WaitForAsync(
            () => Volatile.Read(ref recordingHandler.AssessmentRequests) > 0,
            "the edge runtime to call the remote escalation runtime",
            cancellationToken);

        using var redirect = await edgeHost.Client.GetAsync(
            "/anti-scrape-tarpit/reference/split-test",
            cancellationToken);
        Assert.Equal(HttpStatusCode.TemporaryRedirect, redirect.StatusCode);
        Assert.Equal(
            "http://tarpit.test/tarpit/reference/split-test",
            redirect.Headers.Location?.ToString());

        using var tarpitResponse = await tarpitClient.GetAsync(
            redirect.Headers.Location!.PathAndQuery,
            cancellationToken);
        var html = await tarpitResponse.Content.ReadAsStringAsync(cancellationToken);
        Assert.Equal(HttpStatusCode.OK, tarpitResponse.StatusCode);
        Assert.Equal("text/html", tarpitResponse.Content.Headers.ContentType?.MediaType);
        Assert.Contains("clockwork", html, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task WaitForAsync(
        Func<bool> condition,
        string description,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100, cancellationToken);
        }

        Assert.Fail($"Timed out waiting for {description}.");
    }

    private sealed class RecordingHandler : DelegatingHandler
    {
        public RecordingHandler(HttpMessageHandler innerHandler)
            : base(innerHandler)
        {
        }

        public int AssessmentRequests;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath == "/v1/assess")
            {
                Interlocked.Increment(ref AssessmentRequests);
            }

            return base.SendAsync(request, cancellationToken);
        }
    }
}
