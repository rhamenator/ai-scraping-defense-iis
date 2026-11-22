namespace RedisBlocklistMiddlewareApp.Configuration;

public sealed class DefenseEngineOptions
{
    public const string SectionName = "DefenseEngine";

    public RedisOptions Redis { get; set; } = new();

    public HeuristicOptions Heuristics { get; set; } = new();

    public RobotsOptions Robots { get; set; } = new();

    public NetworkingOptions Networking { get; set; } = new();

    public CloudflareOptions Cloudflare { get; set; } = new();

    public IncidentAlertOptions IncidentAlerts { get; set; } = new();

    public TlsFingerprintOptions TlsFingerprints { get; set; } = new();

    public ManagementOptions Management { get; set; } = new();

    public IntakeOptions Intake { get; set; } = new();

    public AuditOptions Audit { get; set; } = new();

    public EscalationOptions Escalation { get; set; } = new();

    public CommunityBlocklistOptions CommunityBlocklist { get; set; } = new();

    public PublicBlocklistOptions PublicBlocklist { get; set; } = new();

    public PeerSyncOptions PeerSync { get; set; } = new();

    public QueueOptions Queue { get; set; } = new();

    public TarpitOptions Tarpit { get; set; } = new();

    public ObservabilityOptions Observability { get; set; } = new();

    public TopologyOptions Topology { get; set; } = new();

    public ConsensusOptions Consensus { get; set; } = new();
}

public sealed class ConsensusOptions
{
    public bool Enabled { get; set; }

    public string ListenAddress { get; set; } = "0.0.0.0";

    public string AdvertisedHost { get; set; } = string.Empty;

    public int Port { get; set; } = 3262;

    public string StoragePath { get; set; } = "data/raft";

    public string SharedSecret { get; set; } = string.Empty;

    public int RequestTimeoutSeconds { get; set; } = 10;

    public int LowerElectionTimeoutMilliseconds { get; set; } = 600;

    public int UpperElectionTimeoutMilliseconds { get; set; } = 1200;

    public ConsensusMemberOptions[] Members { get; set; } = [];

    /// <summary>
    /// A snapshot of the blocklist is written, and the log before it compacted, after this many committed commands
    /// (#196). Smaller values keep the log and restart time shorter at the cost of more snapshot writes.
    /// </summary>
    public int SnapshotEveryCommands { get; set; } = 1000;
}

public sealed class ConsensusMemberOptions
{
    public string RaftEndpoint { get; set; } = string.Empty;

    public string ApiBaseUrl { get; set; } = string.Empty;
}

public sealed class TopologyOptions
{
    public string Mode { get; set; } = RuntimeTopologyModes.Single;

    public string EscalationBaseUrl { get; set; } = string.Empty;

    public string TarpitPublicBaseUrl { get; set; } = string.Empty;

    public string ServiceToken { get; set; } = string.Empty;

    public int RequestTimeoutSeconds { get; set; } = 10;
}

public static class RuntimeTopologyModes
{
    public const string Single = "Single";

    public const string Split = "Split";
}

public sealed class RedisOptions
{
    public string ConnectionString { get; set; } = "localhost:6379";

    public bool AllowLoopbackConnectionStringInProduction { get; set; }

    public string BlocklistKeyPrefix { get; set; } = "blocklist:ip:";

    public string FrequencyKeyPrefix { get; set; } = "frequency:ip:";

    public string RequestRateKeyPrefix { get; set; } = "rate:ip:";

    public int BlocklistDatabase { get; set; } = 2;

    public int FrequencyDatabase { get; set; } = 3;

    public int BlockDurationMinutes { get; set; } = 1440;

    public int FrequencyWindowSeconds { get; set; } = 60;
}

public sealed class RobotsOptions
{
    /// <summary>
    /// A client that requests a path the loaded robots.txt disallows is blocked.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// robots.txt file loaded at startup, absolute or relative to the application directory.
    /// A missing or unreadable file stops startup so the policy is never silently absent.
    /// Replace the shipped example with the robots.txt of the protected origin.
    /// </summary>
    public string PolicyPath { get; set; } = "Defaults/robots.txt";

    /// <summary>
    /// Serve the loaded policy at /robots.txt so well-behaved crawlers are told what is off limits.
    /// </summary>
    public bool ServePolicy { get; set; } = true;
}

public sealed class HeuristicOptions
{
    /// <summary>
    /// User agents blocked on the first request: named AI crawlers and offensive
    /// scanners. General-purpose HTTP clients belong in <see cref="GenericClientUserAgents"/>.
    /// </summary>
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
        "masscan",
        "zgrab",
        "nmap",
        "sqlmap",
        "nikto"
    ];

    /// <summary>
    /// General-purpose HTTP clients and scraping libraries. A match adds to the score
    /// (it is not a block on its own) and becomes a block when combined with other
    /// signals such as a sensitive path or a high request rate.
    /// </summary>
    public string[] GenericClientUserAgents { get; set; } =
    [
        "curl",
        "wget",
        "python-requests",
        "python-httpx",
        "aiohttp",
        "Go-http-client",
        "libwww-perl",
        "okhttp",
        "Scrapy"
    ];

    /// <summary>
    /// Operators can opt in to the stricter behavior: block generic clients on the first request.
    /// </summary>
    public bool BlockGenericClients { get; set; }

    /// <summary>
    /// Requests per client address within <c>Redis:FrequencyWindowSeconds</c> after which
    /// a <c>high_request_rate</c> signal is raised. Zero disables rate tracking.
    /// </summary>
    public int RequestRateSignalThreshold { get; set; } = 120;

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

    public string[] TrustedCdnProxies { get; set; } = [];

    /// <summary>
    /// Maximum number of X-Forwarded-For hops to consume in TrustedProxy mode. 0 (the default) walks the chain from
    /// the right while each hop is a configured trusted proxy and stops at the first address that is not, which is
    /// the client. Set a positive number to cap it for a deployment with a known, fixed number of proxies.
    /// </summary>
    public int TrustedProxyHopLimit { get; set; }
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

    /// <summary>
    /// When true, the management API and dashboard are served only over HTTPS: in Production the application refuses to
    /// start unless HTTPS is provided (an https:// listener, or <c>Networking:ClientIpResolutionMode=TrustedProxy</c> with
    /// the proxy listed in <c>TrustedProxies</c> so its <c>X-Forwarded-Proto</c> is honoured), and at runtime a management request that did not arrive over
    /// HTTPS is refused with 403 (#186). On by default: management credentials and session cookies must not travel over
    /// plain HTTP. A deployment that really is HTTP-only (local development, a private test network) sets it to false
    /// explicitly, which in Production logs a warning at every start.
    /// </summary>
    public bool RequireSecureTransport { get; set; } = true;
}

public sealed class IntakeOptions
{
    public string ApiKeyHeaderName { get; set; } = "X-Webhook-Key";

    public string ApiKey { get; set; } = string.Empty;

    public IntakeAlertingOptions Alerting { get; set; } = new();

    public CommunityReportingOptions CommunityReporting { get; set; } = new();

    /// <summary>Largest serialized webhook event accepted at /analyze, in bytes (the per-field limits apply first).</summary>
    public int MaxEventBytes { get; set; } = 32 * 1024;
}

public sealed class QueueOptions
{
    public int Capacity { get; set; } = 1024;

    public int WebhookLeaseTimeoutMinutes { get; set; } = 5;

    /// <summary>Attempts before a failing webhook intake item is moved to the dead-letter state.</summary>
    public int WebhookMaxAttempts { get; set; } = 5;

    /// <summary>First retry delay for a failed webhook intake item; doubles on each attempt.</summary>
    public int WebhookRetryBaseMilliseconds { get; set; } = 5000;

    /// <summary>Upper bound for the retry delay.</summary>
    public int WebhookRetryMaxMilliseconds { get; set; } = 300000;
}

public sealed class AuditOptions
{
    public string Provider { get; set; } = AuditStorageProviders.Sqlite;

    public string DatabasePath { get; set; } = "data/defense-events.db";

    public string ConnectionString { get; set; } = string.Empty;

    public int MaxRecentEvents { get; set; } = 500;

    /// <summary>
    /// Most decision rows the SQLite audit store keeps; the oldest are removed beyond it (0 disables the cap). The decision
    /// totals shown on the dashboard are kept separately and are not reduced.
    /// </summary>
    public int MaxStoredEvents { get; set; } = 1_000_000;

    /// <summary>
    /// Delete decision rows older than this many days (0, the default, keeps them until the row cap removes them). Whether
    /// to age out audit history is the operator's policy decision.
    /// </summary>
    public int RetentionDays { get; set; }
}

public sealed class CloudflareOptions
{
    public bool Enabled { get; set; }

    public bool RecommendUnderAttackMode { get; set; } = true;

    public int MinimumRecentDecisions { get; set; } = 50;

    public double MinimumBlockedRate { get; set; } = 0.50d;

    public int MinimumDistinctBlockedOrigins { get; set; } = 20;

    public int AttackWindowMinutes { get; set; } = 5;
}

public sealed class IncidentAlertOptions
{
    public bool Enabled { get; set; } = true;

    public int PollIntervalSeconds { get; set; } = 15;

    public int CooldownMinutes { get; set; } = 30;

    public int OriginHealthTimeoutSeconds { get; set; } = 3;

    public string PrimaryOriginHealthUrl { get; set; } = string.Empty;

    public string BackupOriginHealthUrl { get; set; } = string.Empty;
}

public sealed class TlsFingerprintOptions
{
    public string AttestationKey { get; set; } = string.Empty;

    public string PreviousAttestationKey { get; set; } = string.Empty;

    public int AttestationMaxAgeSeconds { get; set; } = 60;

}

public static class AuditStorageProviders
{
    public const string Sqlite = "Sqlite";

    public const string Postgres = "Postgres";

    public const string SqlServer = "SqlServer";
}

public sealed class EscalationOptions
{
    public ConfiguredRangeReputationOptions ConfiguredRanges { get; set; } = new();

    public HttpReputationProviderOptions HttpReputation { get; set; } = new();

    public LocalTrainedModelOptions LocalTrainedModel { get; set; } = new();

    public OpenAiCompatibleModelAdapterOptions OpenAiCompatibleModel { get; set; } = new();

    public McpModelAdapterOptions McpModel { get; set; } = new();

    public ThreatModelRoutingOptions Routing { get; set; } = new();

    public ContainmentPolicyOptions Containment { get; set; } = new();
}

public sealed class ThreatModelRoutingOptions
{
    public bool Enabled { get; set; } = true;

    public string PreferredPrimaryRoute { get; set; } = ThreatModelRoutes.Auto;

    public bool RemoteFallbackEnabled { get; set; } = true;

    public bool LocalFallbackEnabled { get; set; } = true;

    public int MaxSignalsForLocalRoute { get; set; } = 3;

    public int MaxQueryStringLengthForLocalRoute { get; set; } = 80;

    public int MaxFrequencyForLocalRoute { get; set; } = 4;
}

public static class ThreatModelRoutes
{
    public const string Auto = "Auto";

    public const string Local = "Local";

    public const string Remote = "Remote";

    public const string Any = "Any";
}

public sealed class ContainmentPolicyOptions
{
    public int ChallengeScoreThreshold { get; set; } = 25;

    public int TarpitScoreThreshold { get; set; } = 40;

    public int ThrottleScoreThreshold { get; set; } = 55;

    public int BlockScoreThreshold { get; set; } = 60;

    public int FrequencyBlockThreshold { get; set; } = 8;
}

public static class ContainmentActions
{
    public const string Observed = "observed";

    public const string Challenged = "challenged";

    public const string Tarpitted = "tarpitted";

    public const string Throttled = "throttled";

    public const string Blocked = "blocked";
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

    /// <summary>
    /// Largest positive <c>score_adjustment</c> accepted from the provider's response; larger values are reduced to it.
    /// The provider is a remote service, so its numbers are bounded rather than trusted (#202).
    /// </summary>
    public int MaxScoreAdjustment { get; set; } = 50;

    /// <summary>
    /// Whether a negative <c>score_adjustment</c> from the provider may lower the score. Off by default: a remote service
    /// cannot cancel evidence the engine found itself. Operators who trust the provider can allow it (down to
    /// <c>-MaxScoreAdjustment</c>).
    /// </summary>
    public bool AllowNegativeScoreAdjustments { get; set; }
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

public sealed class LocalTrainedModelOptions
{
    public bool Enabled { get; set; }

    public string ModelPath { get; set; } = "models/local-bot-detector.zip";

    public string MetadataPath { get; set; } = string.Empty;

    public string RequiredModelVersion { get; set; } = string.Empty;

    public float MaliciousProbabilityThreshold { get; set; } = 0.75f;

    public int MaliciousScoreAdjustment { get; set; } = 35;

    public int BenignScoreAdjustment { get; set; } = -10;
}

public sealed class McpModelAdapterOptions
{
    public bool Enabled { get; set; }

    public string ModelUri { get; set; } = string.Empty;

    public string ServerUrl { get; set; } = string.Empty;

    public string AuthToken { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 10;

    public int MaliciousScoreAdjustment { get; set; } = 40;

    public int SuspiciousScoreAdjustment { get; set; } = 25;

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

public sealed class PublicBlocklistOptions
{
    public bool Enabled { get; set; }

    public string ApiKeyHeaderName { get; set; } = "X-API-Key";

    public string ApiKey { get; set; } = string.Empty;

    public int MaximumListEntries { get; set; } = 1000;

    public string ReportReason { get; set; } = "public_blocklist_report";
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

    /// <summary>This node's identity in exported signals. Peers configure it as the peer's Name.</summary>
    public string NodeId { get; set; } = string.Empty;

    /// <summary>Shared secret used to sign exported signals; each importing peer configures it as VerificationKey.</summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>When true (the default) an imported signal must carry a valid signature from a configured peer key.</summary>
    public bool RequireSignedSignals { get; set; } = true;

    /// <summary>Highest hop count accepted on import. 0 accepts only signals the peer originated itself.</summary>
    public int MaximumHops { get; set; }

    public PeerSyncPeerOptions[] Peers { get; set; } = [];
}

public sealed class PeerSyncPeerOptions
{
    public string Name { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string ApiKeyHeaderName { get; set; } = "X-Peer-Key";

    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Shared secret that verifies this peer's signal signatures (the peer's PeerSync:SigningKey).</summary>
    public string VerificationKey { get; set; } = string.Empty;

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

    public string ArchiveDirectory { get; set; } = "data/tarpit-archives";

    public int LinkCount { get; set; } = 6;

    public int ParagraphCount { get; set; } = 5;

    public int ResponseDelayMilliseconds { get; set; } = 200;

    public int MaxHops { get; set; } = 250;

    public int HopWindowSeconds { get; set; } = 86400;

    public int FlaggedIpTtlSeconds { get; set; } = 604800;

    public int RepeatOffenderThreshold { get; set; } = 3;

    public int ArchiveRotationMinutes { get; set; } = 60;

    public int MaximumArchivesToKeep { get; set; } = 5;

    public int JavaScriptDecoyFileCount { get; set; } = 12;

    public int MinJavaScriptDecoyFileSizeKb { get; set; } = 5;

    public int MaxJavaScriptDecoyFileSizeKb { get; set; } = 24;

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

    public SlackAlertOptions Slack { get; set; } = new();

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

    public int TimeoutSeconds { get; set; } = 10;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 587;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public bool UseTls { get; set; } = true;

    public string From { get; set; } = string.Empty;

    public string[] To { get; set; } = [];
}

public sealed class SlackAlertOptions
{
    public bool Enabled { get; set; }

    public string WebhookUrl { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 10;
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
