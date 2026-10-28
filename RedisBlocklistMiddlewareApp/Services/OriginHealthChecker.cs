using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed record OriginHealthSnapshot(
    bool Configured,
    bool PrimaryHealthy,
    bool BackupHealthy,
    string PrimaryHost,
    string BackupHost);

public interface IOriginHealthChecker
{
    Task<OriginHealthSnapshot> CheckAsync(CancellationToken cancellationToken);
}

public sealed class OriginHealthChecker : IOriginHealthChecker
{
    private readonly IncidentAlertOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;

    public OriginHealthChecker(
        IOptions<DefenseEngineOptions> options,
        IHttpClientFactory httpClientFactory)
    {
        _options = options.Value.IncidentAlerts;
        _httpClientFactory = httpClientFactory;
    }

    public async Task<OriginHealthSnapshot> CheckAsync(CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(_options.PrimaryOriginHealthUrl, UriKind.Absolute, out var primary) ||
            !Uri.TryCreate(_options.BackupOriginHealthUrl, UriKind.Absolute, out var backup))
        {
            return new OriginHealthSnapshot(false, false, false, string.Empty, string.Empty);
        }

        var primaryTask = IsHealthyAsync(primary, cancellationToken);
        var backupTask = IsHealthyAsync(backup, cancellationToken);
        await Task.WhenAll(primaryTask, backupTask);
        return new OriginHealthSnapshot(
            true,
            primaryTask.Result,
            backupTask.Result,
            primary.Authority,
            backup.Authority);
    }

    private async Task<bool> IsHealthyAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.OriginHealthTimeoutSeconds));
            using var client = _httpClientFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }
}
