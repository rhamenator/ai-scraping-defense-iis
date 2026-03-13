using System.Net;
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
    private readonly DefenseEngineOptions _options;

    public RedisBlocklistMiddleware(
        RequestDelegate next,
        ILogger<RedisBlocklistMiddleware> logger,
        IBlocklistService blocklistService,
        IRequestSignalEvaluator signalEvaluator,
        ISuspiciousRequestQueue queue,
        IDefenseEventStore eventStore,
        IOptions<DefenseEngineOptions> options)
    {
        _next = next;
        _logger = logger;
        _blocklistService = blocklistService;
        _signalEvaluator = signalEvaluator;
        _queue = queue;
        _eventStore = eventStore;
        _options = options.Value;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (ShouldBypassInspection(context.Request.Path))
        {
            await _next(context);
            return;
        }

        var ipAddress = ResolveRemoteIpAddress(context);
        if (string.IsNullOrWhiteSpace(ipAddress))
        {
            await _next(context);
            return;
        }

        if (await _blocklistService.IsBlockedAsync(ipAddress, context.RequestAborted))
        {
            _logger.LogWarning("Blocking request from IP {IpAddress} via Redis blocklist.", ipAddress);
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Access denied.");
            return;
        }

        var evaluation = _signalEvaluator.Evaluate(context);
        if (evaluation.BlockImmediately)
        {
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
                DateTimeOffset.UtcNow));

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Access denied.");
            return;
        }

        if (evaluation.Signals.Count > 0)
        {
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
                var originalPath = context.Request.Path.HasValue
                    ? context.Request.Path.Value!
                    : "/";

                context.Request.Headers["X-Tarpit-Reason"] = string.Join(';', evaluation.Signals);
                context.Request.Path = new PathString($"{_options.Tarpit.PathPrefix}{originalPath}");
            }
        }

        await _next(context);
    }

    private bool ShouldBypassInspection(PathString path)
    {
        var value = path.Value ?? string.Empty;
        return value.StartsWith(_options.Tarpit.PathPrefix, StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith("/defense", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveRemoteIpAddress(HttpContext context)
    {
        string? value = null;

        if (context.Request.Headers.TryGetValue("X-Forwarded-For", out var forwardedFor))
        {
            value = forwardedFor
                .ToString()
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            value = context.Connection.RemoteIpAddress?.ToString();
        }

        if (IPAddress.TryParse(value, out var address) && address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4().ToString();
        }

        return value;
    }
}
