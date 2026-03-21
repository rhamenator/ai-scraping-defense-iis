using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace AiScrapingDefense.IntegrationTests;

[Collection(EndToEndCollection.Name)]
public sealed class EndToEndFlowTests
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly DefenseStackFixture _fixture;

    public EndToEndFlowTests(DefenseStackFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task SuspiciousRequests_AreAnalyzed_AndEventuallyBlocked()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = await _fixture.CreateHostAsync();
        var client = host.Client;

        using var suspiciousRequest = new HttpRequestMessage(HttpMethod.Get, "/docs");
        suspiciousRequest.Headers.Add(HeaderDrivenClientIpResolver.HeaderName, "198.51.100.51");
        suspiciousRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0");
        suspiciousRequest.Headers.Accept.ParseAdd("*/*");
        var firstResponse = await client.SendAsync(suspiciousRequest, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal("text/html", firstResponse.Content.Headers.ContentType?.MediaType);

        using var secondRequest = new HttpRequestMessage(HttpMethod.Get, "/docs");
        secondRequest.Headers.Add(HeaderDrivenClientIpResolver.HeaderName, "198.51.100.51");
        secondRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0");
        secondRequest.Headers.Accept.ParseAdd("*/*");
        var secondResponse = await client.SendAsync(secondRequest, cancellationToken);

        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);

        var blockedDecision = await WaitForAsync(async () =>
        {
            var decisions = await GetEventsAsync(client, cancellationToken);
            return decisions.FirstOrDefault(decision => string.Equals(decision.Action, "blocked", StringComparison.OrdinalIgnoreCase));
        }, cancellationToken);

        Assert.NotNull(blockedDecision);

        var blockStatus = await GetBlockStatusAsync(client, blockedDecision!.IpAddress, cancellationToken);
        Assert.True(blockStatus.Blocked);

        using var thirdRequest = new HttpRequestMessage(HttpMethod.Get, "/docs");
        thirdRequest.Headers.Add(HeaderDrivenClientIpResolver.HeaderName, "198.51.100.51");
        thirdRequest.Headers.UserAgent.ParseAdd("Mozilla/5.0");
        thirdRequest.Headers.Accept.ParseAdd("*/*");
        var deniedResponse = await client.SendAsync(thirdRequest, cancellationToken);

        Assert.Equal(HttpStatusCode.Forbidden, deniedResponse.StatusCode);
    }

    [Fact]
    public async Task ManualBlocklistEndpoints_Block_And_Unblock()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = await _fixture.CreateHostAsync();
        var client = host.Client;
        const string ipAddress = "198.51.100.44";

        using var blockRequest = CreateManagementRequest(HttpMethod.Post, $"/defense/blocklist?ip={Uri.EscapeDataString(ipAddress)}&reason=manual_test");
        var blockResponse = await client.SendAsync(blockRequest, cancellationToken);
        Assert.True(
            blockResponse.StatusCode == HttpStatusCode.Accepted,
            $"Expected Accepted but received {(int)blockResponse.StatusCode} {blockResponse.StatusCode}: {await blockResponse.Content.ReadAsStringAsync(cancellationToken)}");

        var blockedStatus = await GetBlockStatusAsync(client, ipAddress, cancellationToken);
        Assert.True(blockedStatus.Blocked);

        using var unblockRequest = CreateManagementRequest(HttpMethod.Delete, $"/defense/blocklist?ip={Uri.EscapeDataString(ipAddress)}");
        var unblockResponse = await client.SendAsync(unblockRequest, cancellationToken);
        Assert.Equal(HttpStatusCode.OK, unblockResponse.StatusCode);

        var unblockedStatus = await GetBlockStatusAsync(client, ipAddress, cancellationToken);
        Assert.False(unblockedStatus.Blocked);
    }

    [Fact]
    public async Task WebhookIntake_BlocksIp_AndPersistsDecision()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = await _fixture.CreateHostAsync();
        var client = host.Client;
        const string ipAddress = "198.51.100.77";

        using var request = new HttpRequestMessage(HttpMethod.Post, "/analyze")
        {
            Content = JsonContent.Create(new
            {
                event_type = "ml_verdict",
                reason = "confirmed_bot",
                timestamp_utc = DateTimeOffset.UtcNow,
                details = new
                {
                    ip = ipAddress,
                    method = "GET",
                    path = "/pricing",
                    query_string = "",
                    user_agent = "integration-test",
                    signals = new[] { "model_positive" }
                }
            })
        };
        request.Headers.Add("X-Webhook-Key", _fixture.IntakeApiKey);

        var response = await client.SendAsync(request, cancellationToken);
        Assert.True(
            response.StatusCode == HttpStatusCode.Accepted,
            $"Expected Accepted but received {(int)response.StatusCode} {response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}");

        var blockStatus = await WaitForAsync(async () =>
        {
            var status = await GetBlockStatusAsync(client, ipAddress, cancellationToken);
            return status.Blocked ? status : null;
        }, cancellationToken);

        Assert.NotNull(blockStatus);

        var decisions = await GetEventsAsync(client, cancellationToken);
        Assert.Contains(decisions, decision => decision.IpAddress == ipAddress && decision.Signals.Contains("model_positive"));
    }

    [Fact]
    public async Task Tarpit_UsesPostgresMarkovCorpus_WhenEnabled()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var host = await _fixture.CreateHostAsync();
        var client = host.Client;

        using var response = await client.GetAsync("/anti-scrape-tarpit/reference/manual", cancellationToken);
        var html = await response.Content.ReadAsStringAsync(cancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("clockwork", html, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<DefenseDecisionDto>> GetEventsAsync(HttpClient client, CancellationToken cancellationToken)
    {
        using var request = CreateManagementRequest(HttpMethod.Get, "/defense/events?count=20");
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<IReadOnlyList<DefenseDecisionDto>>(JsonOptions, cancellationToken) ?? [];
    }

    private async Task<BlockStatusDto> GetBlockStatusAsync(HttpClient client, string ipAddress, CancellationToken cancellationToken)
    {
        using var request = CreateManagementRequest(HttpMethod.Get, $"/defense/blocklist?ip={Uri.EscapeDataString(ipAddress)}");
        using var response = await client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<BlockStatusDto>(JsonOptions, cancellationToken))!;
    }

    private HttpRequestMessage CreateManagementRequest(HttpMethod method, string uri)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add("X-API-Key", _fixture.ManagementApiKey);
        return request;
    }

    private static async Task<T?> WaitForAsync<T>(Func<Task<T?>> action, CancellationToken cancellationToken)
        where T : class
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await action();
            if (result is not null)
            {
                return result;
            }

            await Task.Delay(200, cancellationToken);
        }

        return null;
    }

    private sealed record BlockStatusDto(string Ip, bool Blocked);

    private sealed record DefenseDecisionDto(
        string IpAddress,
        string Action,
        int Score,
        long Frequency,
        string Path,
        IReadOnlyList<string> Signals,
        string Summary);
}
