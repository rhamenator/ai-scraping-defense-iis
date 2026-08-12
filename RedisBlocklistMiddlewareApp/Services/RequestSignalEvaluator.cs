using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using RedisBlocklistMiddlewareApp.Configuration;
using RedisBlocklistMiddlewareApp.Models;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class RequestSignalEvaluator : IRequestSignalEvaluator
{
    private readonly HeuristicOptions _options;

    public RequestSignalEvaluator(IOptions<DefenseEngineOptions> options)
    {
        _options = options.Value.Heuristics;
    }

    public RequestSignalEvaluation Evaluate(HttpContext context)
    {
        var userAgent = context.Request.Headers.UserAgent.ToString();
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
