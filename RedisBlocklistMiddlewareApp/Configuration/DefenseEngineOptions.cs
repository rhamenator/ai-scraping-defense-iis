namespace RedisBlocklistMiddlewareApp.Configuration;

public sealed class DefenseEngineOptions
{
    public const string SectionName = "DefenseEngine";

    public RedisOptions Redis { get; set; } = new();

    public HeuristicOptions Heuristics { get; set; } = new();

    public NetworkingOptions Networking { get; set; } = new();

    public ManagementOptions Management { get; set; } = new();

    public AuditOptions Audit { get; set; } = new();

    public QueueOptions Queue { get; set; } = new();

    public TarpitOptions Tarpit { get; set; } = new();
}

public sealed class RedisOptions
{
    public string ConnectionString { get; set; } = "localhost:6379";

    public string BlocklistKeyPrefix { get; set; } = "blocklist:ip:";

    public string FrequencyKeyPrefix { get; set; } = "frequency:ip:";

    public int BlocklistDatabase { get; set; } = 2;

    public int FrequencyDatabase { get; set; } = 3;

    public int BlockDurationMinutes { get; set; } = 240;

    public int FrequencyWindowSeconds { get; set; } = 60;
}

public sealed class HeuristicOptions
{
    public string[] KnownBadUserAgents { get; set; } =
    [
        "GPTBot",
        "CCBot",
        "ClaudeBot",
        "Bytespider",
        "PetalBot",
        "AhrefsBot",
        "SemrushBot",
        "MJ12bot",
        "DotBot",
        "Scrapy",
        "python-requests",
        "curl",
        "wget",
        "masscan",
        "zgrab",
        "nmap",
        "sqlmap",
        "nikto"
    ];

    public string[] SuspiciousPathSubstrings { get; set; } =
    [
        "/wp-admin",
        "/wp-login",
        "/xmlrpc.php",
        "/.env",
        "/phpmyadmin",
        "/graphql",
        "/admin",
        "/actuator"
    ];

    public bool CheckEmptyUserAgent { get; set; } = true;

    public bool CheckMissingAcceptLanguage { get; set; } = true;

    public bool CheckGenericAcceptHeader { get; set; } = true;

    public bool TarpitSuspiciousRequests { get; set; } = true;

    public int BlockScoreThreshold { get; set; } = 60;

    public int FrequencyBlockThreshold { get; set; } = 8;
}

public sealed class NetworkingOptions
{
    public string ClientIpResolutionMode { get; set; } = ClientIpResolutionModes.Direct;

    public string[] TrustedProxies { get; set; } = [];
}

public static class ClientIpResolutionModes
{
    public const string Direct = "Direct";

    public const string TrustedProxy = "TrustedProxy";
}

public sealed class ManagementOptions
{
    public string ApiKeyHeaderName { get; set; } = "X-API-Key";

    public string ApiKey { get; set; } = string.Empty;
}

public sealed class QueueOptions
{
    public int Capacity { get; set; } = 1024;
}

public sealed class AuditOptions
{
    public string DatabasePath { get; set; } = "data/defense-events.db";

    public int MaxRecentEvents { get; set; } = 500;
}

public sealed class TarpitOptions
{
    public string PathPrefix { get; set; } = "/anti-scrape-tarpit";

    public string Seed { get; set; } = "ai-scraping-defense-dotnet";

    public int LinkCount { get; set; } = 6;

    public int ParagraphCount { get; set; } = 5;

    public int ResponseDelayMilliseconds { get; set; } = 200;
}
