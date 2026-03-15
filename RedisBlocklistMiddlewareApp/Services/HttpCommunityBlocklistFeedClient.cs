using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class HttpCommunityBlocklistFeedClient : ICommunityBlocklistFeedClient
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CommunityBlocklistOptions _options;
    private readonly ILogger<HttpCommunityBlocklistFeedClient> _logger;

    public HttpCommunityBlocklistFeedClient(
        IHttpClientFactory httpClientFactory,
        IOptions<DefenseEngineOptions> options,
        ILogger<HttpCommunityBlocklistFeedClient> logger)
    {
        _httpClientFactory = httpClientFactory;
        _options = options.Value.CommunityBlocklist;
        _logger = logger;
    }

    public async Task<IReadOnlyList<string>> FetchAsync(
        CommunityBlocklistSourceOptions source,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source.Url);
        if (!string.IsNullOrWhiteSpace(source.ApiKey))
        {
            request.Headers.TryAddWithoutValidation(source.ApiKeyHeaderName, source.ApiKey);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.RequestTimeoutSeconds)));

        var client = _httpClientFactory.CreateClient(nameof(HttpCommunityBlocklistFeedClient));
        using var response = await client.SendAsync(request, timeoutCts.Token);
        response.EnsureSuccessStatusCode();

        var contentType = response.Content.Headers.ContentType;
        if (IsPlainText(contentType))
        {
            var text = await response.Content.ReadAsStringAsync(timeoutCts.Token);
            return ReadPlainTextIps(text);
        }

        await using var responseStream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
        using var document = await JsonDocument.ParseAsync(responseStream, cancellationToken: timeoutCts.Token);
        var root = document.RootElement;

        if (root.ValueKind == JsonValueKind.Array)
        {
            return ReadIpArray(root);
        }

        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("ips", out var ipsElement) &&
            ipsElement.ValueKind == JsonValueKind.Array)
        {
            return ReadIpArray(ipsElement);
        }

        _logger.LogWarning("Community blocklist feed {Url} returned an unexpected JSON payload.", source.Url);
        return [];
    }

    private static bool IsPlainText(MediaTypeHeaderValue? contentType)
    {
        if (contentType is null)
        {
            return false;
        }

        return string.Equals(contentType.MediaType, "text/plain", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ReadPlainTextIps(string text)
    {
        return text
            .Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToArray();
    }

    private static IReadOnlyList<string> ReadIpArray(JsonElement arrayElement)
    {
        return arrayElement.EnumerateArray()
            .Where(element => element.ValueKind == JsonValueKind.String)
            .Select(element => element.GetString())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
    }
}
