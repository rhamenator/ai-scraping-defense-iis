namespace RedisBlocklistMiddlewareApp.Services;

public class DefenseEngineSyncService : BackgroundService
{
    private static readonly TimeSpan SyncInterval = TimeSpan.FromSeconds(30);

    private readonly ITelemetryService _telemetryService;
    private readonly IPolicyService _policyService;
    private readonly ILogger<DefenseEngineSyncService> _logger;

    public DefenseEngineSyncService(
        ITelemetryService telemetryService,
        IPolicyService policyService,
        ILogger<DefenseEngineSyncService> logger)
    {
        _telemetryService = telemetryService;
        _policyService = policyService;
        _logger = logger;
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

            await Task.Delay(SyncInterval, stoppingToken);
        }

        _logger.LogInformation("DefenseEngineSyncService stopped.");
    }
}
