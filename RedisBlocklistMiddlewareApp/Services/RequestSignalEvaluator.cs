using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class RequestSignalEvaluator : IRequestSignalEvaluator
{
    private readonly HeuristicOptions _options;
    private readonly IRobotsPolicyProvider? _robots;

    public RequestSignalEvaluator(
        IOptions<DefenseEngineOptions> options,
        IRobotsPolicyProvider? robots = null)
    {
        _options = options.Value.Heuristics;
        _robots = robots;
    }

    public RequestSignalEvaluation Evaluate(HttpContext context)
    {
        var userAgent = context.Request.Headers.UserAgent.ToString();

        // A request for a path the site's robots.txt disallows is the primary hard trigger.
        // The User-Agent only selects which rule group applies; it never grants an exemption.
        var robotsRule = _robots?.Policy.DescribeMatchingRule(
            context.Request.Path.Value + context.Request.QueryString.Value,
            userAgent);
        if (robotsRule is not null)
        {
            return new RequestSignalEvaluation(
                true,
                "robots_violation",
                [$"robots_violation:{robotsRule}"]);
        }

        if (!string.IsNullOrWhiteSpace(userAgent))
        {
            var knownBadUserAgent = _options.KnownBadUserAgents.FirstOrDefault(
                candidate => userAgent.Contains(candidate, StringComparison.OrdinalIgnoreCase));
            if (knownBadUserAgent is not null)
            {
                return new RequestSignalEvaluation(
                    true,
                    "known_bad_user_agent",
                    [$"known_bad_user_agent:{knownBadUserAgent}"]);
            }
        }

        var signals = new List<string>();

        var genericClient = string.IsNullOrWhiteSpace(userAgent)
            ? null
            : _options.GenericClientUserAgents.FirstOrDefault(
                candidate => userAgent.Contains(candidate, StringComparison.OrdinalIgnoreCase));
        if (genericClient is not null)
        {
            if (_options.BlockGenericClients)
            {
                return new RequestSignalEvaluation(
                    true,
                    "generic_http_client",
                    [$"generic_http_client:{genericClient}"]);
            }

            // Weak evidence on its own: health checks, monitors and scripts use these clients.
            signals.Add($"generic_http_client:{genericClient}");
        }

        if (_options.CheckEmptyUserAgent && string.IsNullOrWhiteSpace(userAgent))
        {
            signals.Add("empty_user_agent");
        }

        if (_options.CheckMissingAcceptLanguage &&
            !context.Request.Headers.ContainsKey("Accept-Language"))
        {
            signals.Add("missing_accept_language");
        }

        if (_options.CheckGenericAcceptHeader &&
            string.Equals(
                context.Request.Headers.Accept.ToString(),
                "*/*",
                StringComparison.Ordinal))
        {
            signals.Add("generic_accept_any");
        }

        var path = context.Request.Path.Value ?? "/";
        var suspiciousPath = _options.SuspiciousPathSubstrings.FirstOrDefault(
            candidate => path.Contains(candidate, StringComparison.OrdinalIgnoreCase));
        if (suspiciousPath is not null)
        {
            signals.Add($"suspicious_path:{suspiciousPath}");
        }

        if ((context.Request.QueryString.Value?.Length ?? 0) > 200)
        {
            signals.Add("long_query_string");
        }

        return new RequestSignalEvaluation(false, string.Empty, signals);
    }
}
