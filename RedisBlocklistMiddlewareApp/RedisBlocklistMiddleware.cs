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
    private readonly IRequestRateTracker? _rateTracker;

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
        IOptions<DefenseEngineOptions> options,
        IRequestRateTracker? rateTracker = null)
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
        _rateTracker = rateTracker;
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

        // Count every inspected request so volume alone can raise a signal, even when the
        // headers look like a normal browser's.
        var rateThreshold = _options.Heuristics.RequestRateSignalThreshold;
        var requestRate = _rateTracker is not null && rateThreshold > 0
            ? await _rateTracker.IncrementAsync(ipAddress, context.RequestAborted)
            : 0;

        var evaluation = _signalEvaluator.Evaluate(context);
        if (evaluation.BlockImmediately)
        {
            using var activity = _telemetry.StartActivity("edge.immediate_block");
            activity?.SetTag("ip", ipAddress);
            activity?.SetTag("reason", evaluation.BlockReason);

            var blockApplied = await _blocklistService.BlockAsync(
                ipAddress,
                evaluation.BlockReason,
                evaluation.Signals,
                context.RequestAborted);

            if (!blockApplied)
            {
                _logger.LogWarning(
                    "Immediate block was rejected for trusted infrastructure address {IpAddress}; allowing the request to continue.",
                    ipAddress);
                await _next(context);
                return;
            }

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

        var signals = evaluation.Signals;
        var highRate = rateThreshold > 0 && requestRate >= rateThreshold;
        if (highRate && signals.Count == 0)
        {
            // Volume alone must never contain a client: the escalation engine counts every analyzed
            // event toward its own frequency block, so a busy browser or a shared NAT would be blocked
            // for traffic alone. Record one observation per window instead of queueing every request.
            if (requestRate == rateThreshold)
            {
                RecordRateObservation(ipAddress, context.Request.Path.ToString(), requestRate);
            }

            await _next(context);
            return;
        }

        if (highRate)
        {
            signals = [.. signals, $"high_request_rate:{requestRate}"];
        }

        if (signals.Count > 0)
        {
            _telemetry.RecordSuspiciousRequest(signals[0]);
            var queued = await _queue.QueueAsync(
                new SuspiciousRequest(
                    ipAddress,
                    context.Request.Method,
                    context.Request.Path.ToString(),
                    context.Request.QueryString.ToString(),
                    context.Request.Headers.UserAgent.ToString(),
                    signals,
                    DateTimeOffset.UtcNow,
                    TlsFingerprintCaptureMiddleware.GetFingerprint(context)),
                context.RequestAborted);

            if (!queued)
            {
                _logger.LogWarning(
                    "Suspicious request queue dropped item for IP {IpAddress}.",
                    ipAddress);
            }

            // Generic HTTP clients and a high request rate are weak evidence: they are scored by
            // the escalation engine but do not by themselves send a visitor into the tarpit.
            if (_options.Heuristics.TarpitSuspiciousRequests && signals.Any(IsTarpitSignal))
            {
                var originalPath = context.Request.Path.HasValue ? context.Request.Path.Value! : "/";
                if (string.Equals(
                        _options.Topology.Mode,
                        RuntimeTopologyModes.Split,
                        StringComparison.OrdinalIgnoreCase))
                {
                    context.Response.Redirect(
                        BuildSplitTarpitUrl(_options.Topology.TarpitPublicBaseUrl, originalPath),
                        permanent: false,
                        preserveMethod: true);
                    return;
                }
                context.Request.Headers["X-Tarpit-Reason"] = string.Join(';', signals);
                var content = _tarpitPageService.GeneratePage(originalPath, ipAddress);
                _telemetry.RecordTarpitRender();
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/html";
                context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
                await context.Response.WriteAsync(content, context.RequestAborted);
                return;
            }
        }

        await _next(context);
    }

    private void RecordRateObservation(string ipAddress, string path, long requestRate)
    {
        var now = DateTimeOffset.UtcNow;
        string[] signals = [$"high_request_rate:{requestRate}"];
        _eventStore.Add(new DefenseDecision(
            ipAddress,
            "observed",
            30,
            requestRate,
            path,
            signals,
            "High request rate observed; not contained on volume alone.",
            now,
            now,
            new DefenseScoreBreakdown(
                30,
                0,
                30,
                false,
                [
                    new DefenseScoreContribution(
                        "request_rate",
                        30,
                        signals,
                        "Request volume reached the rate signal threshold.")
                ])));
    }

    private static bool IsTarpitSignal(string signal) =>
        !signal.StartsWith("generic_http_client:", StringComparison.Ordinal) &&
        !signal.StartsWith("high_request_rate:", StringComparison.Ordinal);

    internal static string BuildSplitTarpitUrl(string baseUrl, string path)
    {
        var encodedPath = string.Join(
            '/',
            path.Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.EscapeDataString));
        return $"{baseUrl.TrimEnd('/')}/tarpit/{encodedPath}";
    }

    private bool ShouldBypassInspection(PathString path)
    {
        var value = path.Value ?? string.Empty;
        // Crawlers must always be able to read the policy that tells them what is off limits.
        return value.Equals("/robots.txt", StringComparison.OrdinalIgnoreCase) ||
               IsPathOrChild(value, _options.Tarpit.PathPrefix) ||
               IsPathOrChild(value, "/health") ||
               IsPathOrChild(value, "/live") ||
               (_options.Observability.EnablePrometheusEndpoint &&
                IsPathOrChild(value, _options.Observability.PrometheusEndpointPath)) ||
               IsPathOrChild(value, "/defense") ||
               IsPathOrChild(value, "/analyze") ||
               IsPathOrChild(value, "/internal/consensus") ||
               IsPathOrChild(value, "/peer-sync");
    }

    private static bool IsPathOrChild(string value, string prefix)
    {
        var normalizedPrefix = prefix.TrimEnd('/');
        return value.Equals(normalizedPrefix, StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith($"{normalizedPrefix}/", StringComparison.OrdinalIgnoreCase);
    }
}
