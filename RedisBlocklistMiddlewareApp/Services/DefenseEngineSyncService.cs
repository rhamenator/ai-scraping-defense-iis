using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public class DefenseEngineSyncService : BackgroundService
{
    private readonly ITelemetryService _telemetryService;
    private readonly IPolicyService _policyService;
    private readonly ILogger<DefenseEngineSyncService> _logger;
    private readonly DefenseEngineSyncOptions _syncOptions;

    public DefenseEngineSyncService(
        ITelemetryService telemetryService,
        IPolicyService policyService,
        IOptions<DefenseEngineSyncOptions> syncOptions,
        ILogger<DefenseEngineSyncService> logger)
    {
        _telemetryService = telemetryService;
        _policyService = policyService;
        _logger = logger;
        _syncOptions = syncOptions.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("DefenseEngineSyncService started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _telemetryService.RefreshTelemetryAsync(stoppingToken);
                await _policyService.SyncPendingPolicyChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Background sync cycle failed.");
            }

            var interval = TimeSpan.FromSeconds(Math.Max(5, _syncOptions.SyncIntervalSeconds));
            await Task.Delay(interval, stoppingToken);
        }

        _logger.LogInformation("DefenseEngineSyncService stopped.");
    }
}
