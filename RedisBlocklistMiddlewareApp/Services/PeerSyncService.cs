using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class PeerSyncService : BackgroundService
{
    private readonly PeerSyncOptions _options;
    private readonly PeerSyncRunner _runner;
    private readonly ILogger<PeerSyncService> _logger;

    public PeerSyncService(
        IOptions<DefenseEngineOptions> options,
        PeerSyncRunner runner,
        ILogger<PeerSyncService> logger)
    {
        _options = options.Value.PeerSync;
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
                _logger.LogWarning(ex, "Peer sync cycle failed.");
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
