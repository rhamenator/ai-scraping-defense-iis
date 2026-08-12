using System.Diagnostics;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using StackExchange.Redis;

namespace RedisBlocklistMiddlewareApp.Services;

internal sealed class EscalationRedisFrequencyTracker : IRequestFrequencyTracker
{
    private static readonly LuaScript IncrementAndExpireScript = LuaScript.Prepare(
        """
        local current = redis.call('INCR', @key)
        if redis.call('TTL', @key) < 0 then
            redis.call('EXPIRE', @key, @ttlSeconds)
        end
        return current
        """);
    private readonly IConnectionMultiplexer _redis;
    private readonly RedisOptions _options;

    public EscalationRedisFrequencyTracker(
        IConnectionMultiplexer redis,
        IOptions<DefenseEngineOptions> options)
    {
        _redis = redis;
        _options = options.Value.Redis;
    }

    public async Task<long> IncrementAsync(string ipAddress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await _redis.GetDatabase(_options.FrequencyDatabase).ScriptEvaluateAsync(
            IncrementAndExpireScript,
            new
            {
                key = (RedisKey)$"{_options.FrequencyKeyPrefix}{ipAddress}",
                ttlSeconds = Math.Max(1, _options.FrequencyWindowSeconds)
            });
        return (long)result;
    }
}

internal sealed class EscalationAssessmentTelemetry : IAssessmentTelemetry
{
    private static readonly ActivitySource ActivitySource = new("AiScrapingDefense.EscalationEngine");

    public IDisposable? StartActivityScope(string name) => ActivitySource.StartActivity(name);

    public void RecordAssessmentStage(string stage, string result) { }

    public void RecordContributorExecution(string contributorType, string contributorName, string result) { }

    public void RecordRoutingDecision(string primaryRoute, string effectiveRoute, bool fallbackEnabled) { }
}
