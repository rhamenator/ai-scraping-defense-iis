using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using RedisBlocklistMiddlewareApp.Models;

namespace AiScrapingDefense.Contracts;

/// <summary>
/// Keeps credential-bearing URLs out of records, logs and telemetry (#199). Slack, Teams and similar webhook URLs carry
/// their secret in the path, and generic webhooks often carry it in the query or as user info, so only the origin and a
/// short fingerprint of the full URL are ever shown.
/// </summary>
public static partial class UrlRedaction
{
    private const int FingerprintLength = 10;

    // Match as much as could belong to the URL; over-matching is safe because the whole match is replaced.
    [GeneratedRegex(@"https?://[^\s""<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlPattern();

    /// <summary>The origin plus a stable fingerprint, for example <c>https://hooks.example.com (id=3f9a1c07be)</c>.</summary>
    public static string SafeEndpoint(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return string.Empty;
        }

        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..FingerprintLength].ToLowerInvariant();
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsHttp(uri)
            ? $"{Origin(uri)} (id={fingerprint})"
            : $"<invalid-url> (id={fingerprint})";
    }

    /// <summary>
    /// Replaces every URL in <paramref name="text"/> that has user info, a path, a query or a fragment with its safe form.
    /// A bare origin carries no secret and is left alone so ordinary diagnostics stay readable.
    /// </summary>
    public static string RedactUrls(string? text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains("://", StringComparison.Ordinal))
        {
            return text ?? string.Empty;
        }

        return UrlPattern().Replace(text, match =>
        {
            var (candidate, trailing) = SplitTrailing(match.Value);
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || !IsHttp(uri))
            {
                return "<invalid-url>" + trailing;
            }

            return (CarriesSecret(uri) ? SafeEndpoint(candidate) : candidate) + trailing;
        });
    }

    /// <summary>A delivery target: an http(s) URL becomes its safe form, anything else (an email address) is unchanged.</summary>
    public static string SafeTarget(string? target)
    {
        if (string.IsNullOrEmpty(target))
        {
            return string.Empty;
        }

        return target.Contains("://", StringComparison.Ordinal) ? RedactUrls(target) : target;
    }

    /// <summary>The record with its target and detail redacted; applied by every store and on read.</summary>
    public static IntakeDeliveryRecord Redacted(this IntakeDeliveryRecord record) =>
        record with
        {
            Target = SafeTarget(record.Target),
            Detail = RedactUrls(record.Detail)
        };

    private static bool IsHttp(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;

    private static bool CarriesSecret(Uri uri) =>
        !string.IsNullOrEmpty(uri.UserInfo)
        || !string.IsNullOrEmpty(uri.Query)
        || !string.IsNullOrEmpty(uri.Fragment)
        || uri.AbsolutePath != "/";

    private static string Origin(Uri uri) =>
        $"{uri.Scheme}://{uri.Host}{(uri.IsDefaultPort ? string.Empty : ":" + uri.Port)}";

    private static (string Candidate, string Trailing) SplitTrailing(string candidate)
    {
        var trailing = new StringBuilder();
        while (candidate.Length > 0)
        {
            var last = candidate[^1];
            if (".,;:!?'".Contains(last))
            {
                // sentence punctuation, or the closing quote of 'url'; an apostrophe inside the URL stays in the match
            }
            else if (last is ')' or ']' or '}' && IsUnbalancedCloser(candidate, last))
            {
                // a closing bracket with no opener inside the URL belongs to the surrounding text
            }
            else
            {
                break;
            }

            trailing.Insert(0, last);
            candidate = candidate[..^1];
        }

        return (candidate, trailing.ToString());
    }

    private static bool IsUnbalancedCloser(string candidate, char closer)
    {
        var opener = closer switch { ')' => '(', ']' => '[', _ => '{' };
        return candidate.Count(c => c == closer) > candidate.Count(c => c == opener);
    }
}
