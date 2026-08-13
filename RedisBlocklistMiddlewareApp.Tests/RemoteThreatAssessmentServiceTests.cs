using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp.Tests;

public sealed class RemoteThreatAssessmentServiceTests
{
    [Fact]
    public async Task AssessAsync_RetriesTransientFailuresAndAuthenticatesRequest()
    {
        var expected = CreateResult();
        var handler = new SequenceHandler((attempt, request) =>
        {
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            Assert.Equal(new string('s', 32), request.Headers.Authorization?.Parameter);
            return attempt < 3
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        JsonSerializer.Serialize(expected, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
                        Encoding.UTF8,
                        "application/json")
                };
        });
        var service = CreateService(handler);

        var result = await service.AssessAsync(CreateRequest(), TestContext.Current.CancellationToken);

        Assert.Equal(3, handler.Attempts);
        Assert.Equal(expected.Action, result.Action);
        Assert.Equal(expected.Score, result.Score);
    }

    [Fact]
    public async Task AssessAsync_FailsVisiblyAfterBoundedRetries()
    {
        var handler = new SequenceHandler((_, _) =>
            new HttpResponseMessage(HttpStatusCode.BadGateway));
        var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            service.AssessAsync(CreateRequest(), TestContext.Current.CancellationToken));

        Assert.Equal(3, handler.Attempts);
        Assert.Contains("three attempts", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AssessAsync_AttestsOnlyServerVerifiedTlsFingerprint()
    {
        const string key = "0123456789abcdef0123456789abcdef";
        SuspiciousRequest? forwarded = null;
        var handler = new SequenceHandler((_, message) =>
        {
            var json = message.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            forwarded = JsonSerializer.Deserialize<SuspiciousRequest>(
                json,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(CreateResult())
            };
        });
        var service = CreateService(handler, key);
        var request = CreateRequest() with
        {
            TlsFingerprint = new TlsClientFingerprint(
                "72a589da586844d7f0818ce684948eea",
                "t13d1516h2_8daaf6152771_e5627efa2ab1",
                "envoy",
                Verified: true)
        };

        await service.AssessAsync(request, TestContext.Current.CancellationToken);

        Assert.NotNull(forwarded?.TlsFingerprint?.Attestation);
        Assert.False(forwarded!.TlsFingerprint!.Verified);
        Assert.True(Security.TlsFingerprintAttestation.Verify(
            forwarded.TlsFingerprint,
            forwarded.IpAddress,
            forwarded.Method,
            forwarded.Path,
            key,
            60));
    }

    private static RemoteThreatAssessmentService CreateService(
        HttpMessageHandler handler,
        string attestationKey = "")
    {
        var options = Options.Create(new DefenseEngineOptions
        {
            Topology = new TopologyOptions
            {
                Mode = RuntimeTopologyModes.Split,
                EscalationBaseUrl = "http://escalation.test",
                ServiceToken = new string('s', 32),
                RequestTimeoutSeconds = 2
            },
            TlsFingerprints = new TlsFingerprintOptions
            {
                AttestationKey = attestationKey,
                AttestationMaxAgeSeconds = 60
            }
        });
        return new RemoteThreatAssessmentService(new HttpClient(handler), options);
    }

    private static SuspiciousRequest CreateRequest() =>
        new(
            "198.51.100.20",
            "GET",
            "/products",
            string.Empty,
            "test-agent",
            ["signal"],
            DateTimeOffset.UtcNow);

    private static ThreatAssessmentResult CreateResult() =>
        new(
            ContainmentActions.Observed,
            false,
            "score_thresholds",
            "Observed remotely.",
            10,
            1,
            ["signal"],
            new DefenseScoreBreakdown(10, 0, 10, false, []));

    private sealed class SequenceHandler(
        Func<int, HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public int Attempts { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Attempts++;
            return Task.FromResult(responder(Attempts, request));
        }
    }
}
