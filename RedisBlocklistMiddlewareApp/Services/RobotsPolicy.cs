using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RedisBlocklistMiddlewareApp.Services;

/// <summary>
/// A parsed robots.txt (RFC 9309). The group chosen for a client comes from its
/// User-Agent header; that is policy selection, never identity trust.
/// </summary>
public sealed partial class RobotsPolicy
{
    private readonly List<Group> _groups;

    private RobotsPolicy(List<Group> groups)
    {
        _groups = groups;
    }

    public static RobotsPolicy Empty { get; } = new([]);

    public bool HasRules => _groups.Any(group => group.Rules.Count > 0);

    public static RobotsPolicy Parse(string text)
    {
        var groups = new List<Group>();
        Group? current = null;
        text = text.TrimStart('﻿');
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.None))
        {
            var hash = rawLine.IndexOf('#');
            var line = hash >= 0 ? rawLine[..hash] : rawLine;
            var match = DirectiveRegex().Match(line);
            if (!match.Success)
            {
                continue;
            }

            var key = match.Groups[1].Value.ToLowerInvariant();
            var value = match.Groups[2].Value;
            if (key == "user-agent")
            {
                current = StartGroup(groups, current, value);
            }
            else if (current is not null && (key == "allow" || key == "disallow"))
            {
                current.HasRules = true;
                var rule = BuildRule(key == "allow", value);
                if (rule is not null)
                {
                    current.Rules.Add(rule);
                }
            }
        }

        return new RobotsPolicy(groups);
    }

    /// <summary>
    /// True when the policy forbids this request path (and query) for this user agent.
    /// /robots.txt itself is always permitted.
    /// </summary>
    public bool IsDisallowed(string? pathAndQuery, string? userAgent)
    {
        if (string.IsNullOrEmpty(pathAndQuery))
        {
            return false;
        }

        var path = Normalize(pathAndQuery, isPattern: false);
        if (path == "/robots.txt" || path.StartsWith("/robots.txt?", StringComparison.Ordinal))
        {
            return false;
        }

        var groups = SelectGroups((userAgent ?? string.Empty).ToLowerInvariant());
        return !VerdictAllows(groups, path);
    }

    /// <summary>The rule that decided a disallowed request, for the audit trail.</summary>
    public string? DescribeMatchingRule(string? pathAndQuery, string? userAgent)
    {
        if (!IsDisallowed(pathAndQuery, userAgent))
        {
            return null;
        }

        var path = Normalize(pathAndQuery!, isPattern: false);
        var groups = SelectGroups((userAgent ?? string.Empty).ToLowerInvariant());
        Rule? best = null;
        foreach (var rule in groups.SelectMany(group => group.Rules))
        {
            if (Matches(path, rule) && (best is null || rule.Length >= best.Length))
            {
                best = rule;
            }
        }

        return best is null ? null : $"{(best.Allow ? "Allow" : "Disallow")}: {best.Pattern}{(best.Anchored ? "$" : string.Empty)}";
    }

    private static Group? StartGroup(List<Group> groups, Group? current, string value)
    {
        value = value.ToLowerInvariant();
        if (value != "*" && !AgentTokenRegex().IsMatch(value))
        {
            return current;
        }

        if (current is null || current.HasRules)
        {
            current = new Group();
            groups.Add(current);
        }

        current.Agents.Add(value);
        return current;
    }

    private static Rule? BuildRule(bool allow, string value)
    {
        if (value.Length == 0 || (value[0] != '/' && value[0] != '*'))
        {
            return null;
        }

        var anchored = value[^1] == '$';
        if (anchored)
        {
            value = value[..^1];
        }

        value = Normalize(value, isPattern: true);
        var octets = PercentTripletRegex().Replace(value, "X").Replace("*", string.Empty).Length;
        // RFC 9309: the longest pattern is the most specific. The "$" is part of the
        // pattern, so an anchored rule outranks the same text without it.
        return new Rule(value, anchored, octets + (anchored ? 1 : 0), allow);
    }

    private List<Group> SelectGroups(string userAgentLower)
    {
        var specific = new List<Group>();
        var wildcard = new List<Group>();
        foreach (var group in _groups)
        {
            var selected = false;
            var fallback = false;
            foreach (var agent in group.Agents)
            {
                if (agent == "*")
                {
                    fallback = true;
                }
                else if (ContainsToken(userAgentLower, agent))
                {
                    selected = true;
                }
            }

            if (selected)
            {
                specific.Add(group);
            }

            if (fallback)
            {
                wildcard.Add(group);
            }
        }

        return specific.Count > 0 ? specific : wildcard;
    }

    private static bool VerdictAllows(List<Group> groups, string path)
    {
        var best = -1;
        var allowed = true;
        foreach (var rule in groups.SelectMany(group => group.Rules).Where(rule => rule.Length >= best && Matches(path, rule)))
        {
            allowed = rule.Length > best ? rule.Allow : allowed || rule.Allow;
            best = rule.Length;
        }

        return allowed;
    }

    private static bool ContainsToken(string haystack, string token)
    {
        var start = 0;
        while ((start = haystack.IndexOf(token, start, StringComparison.Ordinal)) >= 0)
        {
            var end = start + token.Length;
            var before = start == 0 || !IsTokenChar(haystack[start - 1]);
            var after = end >= haystack.Length || !IsTokenChar(haystack[end]);
            if (before && after)
            {
                return true;
            }

            start++;
        }

        return false;
    }

    private static bool IsTokenChar(char ch) => ch is >= 'a' and <= 'z' or '_' or '-';

    // Greedy wildcard matching avoids recursive pattern backtracking.
    private static bool Matches(string path, Rule rule)
    {
        var pattern = rule.Anchored ? rule.Pattern : rule.Pattern + "*";
        int i = 0, j = 0, star = -1, resume = 0;
        while (i < path.Length)
        {
            if (j < pattern.Length && pattern[j] == '*')
            {
                star = j;
                resume = i;
                j++;
            }
            else if (j < pattern.Length && pattern[j] == path[i])
            {
                i++;
                j++;
            }
            else if (star >= 0)
            {
                resume++;
                i = resume;
                j = star + 1;
            }
            else
            {
                return false;
            }
        }

        while (j < pattern.Length && pattern[j] == '*')
        {
            j++;
        }

        return j >= pattern.Length;
    }

    /// <summary>
    /// Percent-decodes unreserved bytes, upper-cases other escapes and encodes anything
    /// else outside printable ASCII. Case is preserved: robots paths are case-sensitive.
    /// </summary>
    private static string Normalize(string value, bool isPattern)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        var result = new StringBuilder(bytes.Length);
        for (var i = 0; i < bytes.Length; i++)
        {
            var ch = (char)bytes[i];
            if (ch == '%' && i + 2 < bytes.Length && IsHex(bytes[i + 1]) && IsHex(bytes[i + 2]))
            {
                var decoded = (char)byte.Parse(
                    Encoding.ASCII.GetString(bytes, i + 1, 2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture);
                if (IsUnreserved(decoded))
                {
                    result.Append(decoded);
                }
                else
                {
                    result.Append('%').Append(Encoding.ASCII.GetString(bytes, i + 1, 2).ToUpperInvariant());
                }

                i += 2;
            }
            else if (bytes[i] > 127 || bytes[i] <= 32 || ch == '$' || (ch == '*' && !isPattern))
            {
                result.Append('%').Append(bytes[i].ToString("X2", CultureInfo.InvariantCulture));
            }
            else
            {
                result.Append(ch);
            }
        }

        return result.ToString();
    }

    private static bool IsHex(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';

    private static bool IsUnreserved(char ch) =>
        ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_' or '~' or '-';

    [GeneratedRegex(@"^\s*([A-Za-z\-]+)\s*:\s*(.*?)\s*$")]
    private static partial Regex DirectiveRegex();

    [GeneratedRegex(@"^[a-z_\-]+$")]
    private static partial Regex AgentTokenRegex();

    [GeneratedRegex(@"%[0-9A-Fa-f]{2}")]
    private static partial Regex PercentTripletRegex();

    private sealed class Group
    {
        public List<string> Agents { get; } = [];

        public List<Rule> Rules { get; } = [];

        public bool HasRules { get; set; }
    }

    private sealed record Rule(string Pattern, bool Anchored, int Length, bool Allow);
}
