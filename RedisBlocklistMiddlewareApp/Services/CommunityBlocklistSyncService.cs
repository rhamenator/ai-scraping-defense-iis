using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class CommunityBlocklistSyncService : BackgroundService
{
    private readonly CommunityBlocklistOptions _options;
    private readonly CommunityBlocklistSyncRunner _runner;
    private readonly ILogger<CommunityBlocklistSyncService> _logger;

    public CommunityBlocklistSyncService(
        IOptions<DefenseEngineOptions> options,
        CommunityBlocklistSyncRunner runner,
        ILogger<CommunityBlocklistSyncService> logger)
    {
        _options = options.Value.CommunityBlocklist;
        _runner = runner;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _runner.RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Community blocklist sync cycle failed.");
            }

            try
            {
                await Task.Delay(
                    TimeSpan.FromMinutes(Math.Max(1, _options.SyncIntervalMinutes)),
                    stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
