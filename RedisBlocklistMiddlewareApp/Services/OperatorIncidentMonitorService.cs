using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using StackExchange.Redis;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class OperatorIncidentMonitorService : BackgroundService
{
    private readonly DefenseEngineOptions _options;
    private readonly IDefenseEventStore _eventStore;
    private readonly IRedisConnectionProvider _redisConnectionProvider;
    private readonly IOriginHealthChecker _originHealthChecker;
    private readonly IOperatorIncidentAlertDispatcher _alertDispatcher;
    private readonly IIntakeDeliveryStore _deliveryStore;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OperatorIncidentMonitorService> _logger;

    public OperatorIncidentMonitorService(
        IOptions<DefenseEngineOptions> options,
        IDefenseEventStore eventStore,
        IRedisConnectionProvider redisConnectionProvider,
        IOriginHealthChecker originHealthChecker,
        IOperatorIncidentAlertDispatcher alertDispatcher,
        IIntakeDeliveryStore deliveryStore,
        TimeProvider timeProvider,
        ILogger<OperatorIncidentMonitorService> logger)
    {
        _options = options.Value;
        _eventStore = eventStore;
        _redisConnectionProvider = redisConnectionProvider;
        _originHealthChecker = originHealthChecker;
        _alertDispatcher = alertDispatcher;
        _deliveryStore = deliveryStore;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.IncidentAlerts.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(_options.IncidentAlerts.PollIntervalSeconds));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await CheckOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Distributed attack incident check failed.");
            }
        }
    }

    public async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        if (!_options.IncidentAlerts.Enabled || !HasConfiguredChannel())
        {
            return;
        }

        var signal = DistributedAttackSignalEvaluator.Evaluate(
            _eventStore.GetRecent(500),
            _options.Cloudflare,
            _timeProvider.GetUtcNow());
        if (signal is null)
        {
            return;
        }

        var redis = await _redisConnectionProvider.GetAsync(cancellationToken);
        var database = redis.GetDatabase(_options.Redis.BlocklistDatabase);
        var key = $"{_options.Redis.BlocklistKeyPrefix}incident:distributed-attack:alert";
        var token = Guid.NewGuid().ToString("N");
        var acquired = await database.StringSetAsync(
            key,
            token,
            TimeSpan.FromMinutes(_options.IncidentAlerts.CooldownMinutes),
            When.NotExists).WaitAsync(cancellationToken);
        if (!acquired)
        {
            return;
        }

        var delivered = false;
        try
        {
            var origins = await _originHealthChecker.CheckAsync(cancellationToken);
            var choices = new List<string>
            {
                "Review the recent decisions in the AI Scraping Defense dashboard."
            };
            if (_options.Cloudflare.Enabled && _options.Cloudflare.RecommendUnderAttackMode)
            {
                choices.Add("If the attack is ongoing, manually enable Cloudflare Under Attack Mode for the affected zone.");
            }
            if (origins.Configured && !origins.PrimaryHealthy && origins.BackupHealthy)
            {
                choices.Add($"Manually fail over from unhealthy primary {origins.PrimaryHost} to healthy backup {origins.BackupHost}.");
            }

            var alert = new OperatorIncidentAlert(signal, origins, choices);
            var records = await _alertDispatcher.DispatchAsync(alert, cancellationToken);
            delivered = records.Any(record => record.Status == IntakeDeliveryStatuses.Succeeded);
            foreach (var record in records)
            {
                _deliveryStore.Add(record);
            }
            if (delivered)
            {
                _logger.LogWarning(
                    "Distributed attack incident alert delivered: {BlockedCount}/{DecisionCount} blocked across {OriginCount} origins.",
                    signal.BlockedCount,
                    signal.DecisionCount,
                    signal.DistinctBlockedOrigins);
            }
            else
            {
                _logger.LogWarning("Distributed attack incident alert had no successful delivery; it will be retried.");
            }
        }
        finally
        {
            if (!delivered)
            {
                await database.LockReleaseAsync(key, token);
            }
        }
    }

    private bool HasConfiguredChannel()
    {
        var alerting = _options.Intake.Alerting;
        return alerting.GenericWebhook.Enabled && !string.IsNullOrWhiteSpace(alerting.GenericWebhook.Url) ||
               alerting.Slack.Enabled && !string.IsNullOrWhiteSpace(alerting.Slack.WebhookUrl) ||
               alerting.Smtp.Enabled && !string.IsNullOrWhiteSpace(alerting.Smtp.Host) &&
               !string.IsNullOrWhiteSpace(alerting.Smtp.From) && alerting.Smtp.To.Length > 0;
    }
}
