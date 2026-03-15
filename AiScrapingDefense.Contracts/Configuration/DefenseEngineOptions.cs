namespace RedisBlocklistMiddlewareApp.Configuration;

public sealed class DefenseEngineOptions
{
    public const string SectionName = "DefenseEngine";

    public RedisOptions Redis { get; set; } = new();

    public HeuristicOptions Heuristics { get; set; } = new();

    public NetworkingOptions Networking { get; set; } = new();

    public ManagementOptions Management { get; set; } = new();

    public IntakeOptions Intake { get; set; } = new();

    public AuditOptions Audit { get; set; } = new();

    public EscalationOptions Escalation { get; set; } = new();

    public CommunityBlocklistOptions CommunityBlocklist { get; set; } = new();

    public PeerSyncOptions PeerSync { get; set; } = new();

    public QueueOptions Queue { get; set; } = new();

    public TarpitOptions Tarpit { get; set; } = new();

    public ObservabilityOptions Observability { get; set; } = new();
}

public sealed class RedisOptions
{
    public string ConnectionString { get; set; } = "localhost:6379";

    public bool AllowLoopbackConnectionStringInProduction { get; set; }

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

    public int DashboardSessionHours { get; set; } = 8;
}

public sealed class IntakeOptions
{
    public string ApiKeyHeaderName { get; set; } = "X-Webhook-Key";

    public string ApiKey { get; set; } = string.Empty;

    public IntakeAlertingOptions Alerting { get; set; } = new();

    public CommunityReportingOptions CommunityReporting { get; set; } = new();
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

public sealed class EscalationOptions
{
    public ConfiguredRangeReputationOptions ConfiguredRanges { get; set; } = new();

    public HttpReputationProviderOptions HttpReputation { get; set; } = new();

    public OpenAiCompatibleModelAdapterOptions OpenAiCompatibleModel { get; set; } = new();
}

public sealed class ConfiguredRangeReputationOptions
{
    public bool Enabled { get; set; }

    public ReputationRangeEntry[] Entries { get; set; } = [];
}

public sealed class ReputationRangeEntry
{
    public string Name { get; set; } = string.Empty;

    public string Cidr { get; set; } = string.Empty;

    public int ScoreAdjustment { get; set; } = 0;

    public string[] Signals { get; set; } = [];
}

public sealed class HttpReputationProviderOptions
{
    public bool Enabled { get; set; }

    public string Endpoint { get; set; } = string.Empty;

    public string ApiKeyHeaderName { get; set; } = "X-Api-Key";

    public string ApiKey { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 10;

    public int MaliciousScoreAdjustment { get; set; } = 35;
}

public sealed class OpenAiCompatibleModelAdapterOptions
{
    public bool Enabled { get; set; }

    public string Endpoint { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    public string SystemPrompt { get; set; } =
        "You are classifying incoming web requests for scraping-defense enforcement. " +
        "Return JSON with classification and summary. Classification must be one of " +
        "MALICIOUS_BOT, BENIGN_CRAWLER, HUMAN, or INCONCLUSIVE.";

    public int TimeoutSeconds { get; set; } = 20;

    public int MaliciousScoreAdjustment { get; set; } = 40;

    public int BenignCrawlerScoreAdjustment { get; set; } = -5;

    public int HumanScoreAdjustment { get; set; } = -15;
}

public sealed class CommunityBlocklistOptions
{
    public bool Enabled { get; set; }

    public int SyncIntervalMinutes { get; set; } = 60;

    public int RequestTimeoutSeconds { get; set; } = 10;

    public int MaximumEntriesPerSource { get; set; } = 5000;

    public CommunityBlocklistSourceOptions[] Sources { get; set; } = [];
}

public sealed class CommunityBlocklistSourceOptions
{
    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string ApiKeyHeaderName { get; set; } = "X-API-Key";

    public string ApiKey { get; set; } = string.Empty;
}

public sealed class PeerSyncOptions
{
    public bool Enabled { get; set; }

    public int SyncIntervalMinutes { get; set; } = 60;

    public int RequestTimeoutSeconds { get; set; } = 10;

    public int MaximumSignalsPerPeer { get; set; } = 500;

    public int MaximumExportSignals { get; set; } = 500;

    public string ExportApiKeyHeaderName { get; set; } = "X-Peer-Key";

    public string ExportApiKey { get; set; } = string.Empty;

    public PeerSyncPeerOptions[] Peers { get; set; } = [];
}

public sealed class PeerSyncPeerOptions
{
    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string ApiKeyHeaderName { get; set; } = "X-Peer-Key";

    public string ApiKey { get; set; } = string.Empty;

    public string TrustMode { get; set; } = PeerTrustModes.ObserveOnly;
}

public static class PeerTrustModes
{
    public const string ObserveOnly = "ObserveOnly";

    public const string BlockList = "BlockList";
}

public sealed class TarpitOptions
{
    public string PathPrefix { get; set; } = "/anti-scrape-tarpit";

    public string Seed { get; set; } = "ai-scraping-defense-dotnet";

    public int LinkCount { get; set; } = 6;

    public int ParagraphCount { get; set; } = 5;

    public int ResponseDelayMilliseconds { get; set; } = 200;

    public string[] Modes { get; set; } =
    [
        TarpitRenderModes.Standard,
        TarpitRenderModes.ArchiveIndex,
        TarpitRenderModes.ApiCatalog
    ];

    public int MarkovWordsPerParagraph { get; set; } = 36;

    public PostgresMarkovOptions PostgresMarkov { get; set; } = new();
}

public sealed class ObservabilityOptions
{
    public string ServiceName { get; set; } = "ai-scraping-defense-dotnet";

    public bool EnablePrometheusEndpoint { get; set; } = true;

    public string PrometheusEndpointPath { get; set; } = "/metrics";

    public string OtlpEndpoint { get; set; } = string.Empty;
}

public sealed class IntakeAlertingOptions
{
    public GenericWebhookAlertOptions GenericWebhook { get; set; } = new();

    public SmtpAlertOptions Smtp { get; set; } = new();
}

public sealed class GenericWebhookAlertOptions
{
    public bool Enabled { get; set; }

    public string Url { get; set; } = string.Empty;

    public string AuthorizationHeaderValue { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 10;
}

public sealed class SmtpAlertOptions
{
    public bool Enabled { get; set; }

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public bool UseTls { get; set; } = true;

    public string From { get; set; } = string.Empty;

    public string[] To { get; set; } = [];
}

public sealed class CommunityReportingOptions
{
    public bool Enabled { get; set; }

    public string ProviderName { get; set; } = "AbuseIPDB";

    public string Endpoint { get; set; } = string.Empty;

    public string ApiKeyHeaderName { get; set; } = "Key";

    public string ApiKey { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 10;

    public string DefaultCategories { get; set; } = "19";

    public string CommentPrefix { get; set; } = "AI Defense Stack detection";
}

public sealed class PostgresMarkovOptions
{
    public bool Enabled { get; set; }

    public string ConnectionString { get; set; } = string.Empty;

    public string WordsTableName { get; set; } = "markov_words";

    public string SequencesTableName { get; set; } = "markov_sequences";

    public int RefreshMinutes { get; set; } = 30;
}

public static class TarpitRenderModes
{
    public const string Standard = "Standard";

    public const string ArchiveIndex = "ArchiveIndex";

    public const string ApiCatalog = "ApiCatalog";
}
