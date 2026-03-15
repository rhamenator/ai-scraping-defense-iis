using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;
using RedisBlocklistMiddlewareApp.Services;

namespace RedisBlocklistMiddlewareApp;

public sealed class RedisBlocklistMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<RedisBlocklistMiddleware> _logger;
    private readonly IBlocklistService _blocklistService;
    private readonly IRequestSignalEvaluator _signalEvaluator;
    private readonly ISuspiciousRequestQueue _queue;
    private readonly IDefenseEventStore _eventStore;
    private readonly IClientIpResolver _clientIpResolver;
    private readonly ITarpitPageService _tarpitPageService;
    private readonly DefenseTelemetry _telemetry;
    private readonly DefenseEngineOptions _options;

    public RedisBlocklistMiddleware(
        RequestDelegate next,
        ILogger<RedisBlocklistMiddleware> logger,
        IBlocklistService blocklistService,
        IRequestSignalEvaluator signalEvaluator,
        ISuspiciousRequestQueue queue,
        IDefenseEventStore eventStore,
        IClientIpResolver clientIpResolver,
        ITarpitPageService tarpitPageService,
        DefenseTelemetry telemetry,
        IOptions<DefenseEngineOptions> options)
    {
        _next = next;
        _logger = logger;
        _blocklistService = blocklistService;
        _signalEvaluator = signalEvaluator;
        _queue = queue;
        _eventStore = eventStore;
        _clientIpResolver = clientIpResolver;
        _tarpitPageService = tarpitPageService;
        _telemetry = telemetry;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (ShouldBypassInspection(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var ipAddress = _clientIpResolver.Resolve(context);
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            await _next(context);
            return;
        }

        if (await _blocklistService.IsBlockedAsync(ipAddress, context.RequestAborted))
        {
            _logger.LogWarning("Blocking request from IP {IpAddress} via Redis blocklist.", ipAddress);
            _telemetry.RecordDecision("blocked", "redis_blocklist");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Access denied.");
            return;
        }

        var evaluation = _signalEvaluator.Evaluate(context);
        if (evaluation.BlockImmediately)
        {
            using var activity = _telemetry.StartActivity("edge.immediate_block");
            activity?.SetTag("ip", ipAddress);
            activity?.SetTag("reason", evaluation.BlockReason);

            await _blocklistService.BlockAsync(
                ipAddress,
                evaluation.BlockReason,
                evaluation.Signals,
                context.RequestAborted);

            _eventStore.Add(new DefenseDecision(
                ipAddress,
                "blocked",
                100,
                1,
                context.Request.Path.ToString(),
                evaluation.Signals,
                "Blocked immediately by edge heuristics.",
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                new DefenseScoreBreakdown(
                    100,
                    0,
                    100,
                    true,
                    [
                        new DefenseScoreContribution(
                            "edge_heuristics",
                            100,
                            evaluation.Signals,
                            "Edge heuristics produced an immediate block verdict.")
                    ])));

            _telemetry.RecordDecision("blocked", "edge_heuristics");
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Access denied.");
            return;
        }

        if (evaluation.Signals.Count > 0)
        {
            _telemetry.RecordSuspiciousRequest(evaluation.Signals[0]);
            var queued = await _queue.QueueAsync(
                new SuspiciousRequest(
                    ipAddress,
                    context.Request.Method,
                    context.Request.Path.ToString(),
                    context.Request.QueryString.ToString(),
                    context.Request.Headers.UserAgent.ToString(),
                    evaluation.Signals,
                    DateTimeOffset.UtcNow),
                context.RequestAborted);

            if (!queued)
            {
                _logger.LogWarning(
                    "Suspicious request queue dropped item for IP {IpAddress}.",
                    ipAddress);
            }

            if (_options.Heuristics.TarpitSuspiciousRequests)
            {
                var originalPath = context.Request.Path.HasValue ? context.Request.Path.Value! : "/";
                context.Request.Headers["X-Tarpit-Reason"] = string.Join(';', evaluation.Signals);
                var content = _tarpitPageService.GeneratePage(originalPath, ipAddress);
                _telemetry.RecordTarpitRender();
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/html";
                await context.Response.WriteAsync(content, context.RequestAborted);
                return;
            }
        }

        await _next(context);
    }

    private bool ShouldBypassInspection(PathString path)
    {
        var value = path.Value ?? string.Empty;
        return value.StartsWith(_options.Tarpit.PathPrefix, StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
               (_options.Observability.EnablePrometheusEndpoint &&
                value.StartsWith(_options.Observability.PrometheusEndpointPath, StringComparison.OrdinalIgnoreCase)) ||
               value.StartsWith("/defense", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("/analyze", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("/peer-sync", StringComparison.OrdinalIgnoreCase);
    }
}
