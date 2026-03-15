using System.Diagnostics;
using Microsoft.Extensions.Options;
using Prometheus;
using RedisBlocklistMiddlewareApp.Configuration;

namespace RedisBlocklistMiddlewareApp.Services;

public sealed class DefenseTelemetry
{
    public const string ActivitySourceName = "AiScrapingDefense";

    private static readonly ActivitySource SharedActivitySource = new(ActivitySourceName);
    private static readonly Counter SuspiciousRequests = Metrics.CreateCounter(
        "ai_scraping_defense_suspicious_requests_total",
        "Suspicious requests observed by the edge middleware.",
        new CounterConfiguration
        {
            LabelNames = ["reason"]
        });
    private static readonly Counter BlockDecisions = Metrics.CreateCounter(
        "ai_scraping_defense_block_decisions_total",
        "Defense decisions that resulted in blocking.",
        new CounterConfiguration
        {
            LabelNames = ["source"]
        });
    private static readonly Counter ObservedDecisions = Metrics.CreateCounter(
        "ai_scraping_defense_observed_decisions_total",
        "Defense decisions retained without immediate blocking.",
        new CounterConfiguration
        {
            LabelNames = ["source"]
        });
    private static readonly Counter TarpitRenders = Metrics.CreateCounter(
        "ai_scraping_defense_tarpit_renders_total",
        "Rendered tarpit responses.");
    private static readonly Counter WebhookAccepted = Metrics.CreateCounter(
        "ai_scraping_defense_webhook_intake_total",
        "Webhook intake events accepted for durable processing.");
    private static readonly Counter IntakeDeliveryAttempts = Metrics.CreateCounter(
        "ai_scraping_defense_intake_delivery_total",
        "Intake alert/report delivery outcomes.",
        new CounterConfiguration
        {
            LabelNames = ["delivery_type", "channel", "status"]
        });
    private static readonly Counter CommunityImports = Metrics.CreateCounter(
        "ai_scraping_defense_community_imports_total",
        "Community-blocklist import outcomes.",
        new CounterConfiguration
        {
            LabelNames = ["result"]
        });
    private static readonly Counter PeerImports = Metrics.CreateCounter(
        "ai_scraping_defense_peer_imports_total",
        "Peer-sync import outcomes.",
        new CounterConfiguration
        {
            LabelNames = ["result"]
        });

    private readonly ObservabilityOptions _options;

    public DefenseTelemetry(IOptions<DefenseEngineOptions> options)
    {
        _options = options.Value.Observability;
    }

    public string ServiceName => _options.ServiceName;

    public Activity? StartActivity(string name, ActivityKind kind = ActivityKind.Internal)
    {
        return SharedActivitySource.StartActivity(name, kind);
    }

    public void RecordSuspiciousRequest(string reason)
    {
        SuspiciousRequests.WithLabels(SanitizeLabel(reason)).Inc();
    }

    public void RecordDecision(string action, string source)
    {
        if (string.Equals(action, "blocked", StringComparison.OrdinalIgnoreCase))
        {
            BlockDecisions.WithLabels(SanitizeLabel(source)).Inc();
            return;
        }

        ObservedDecisions.WithLabels(SanitizeLabel(source)).Inc();
    }

    public void RecordTarpitRender()
    {
        TarpitRenders.Inc();
    }

    public void RecordWebhookAccepted()
    {
        WebhookAccepted.Inc();
    }

    public void RecordIntakeDelivery(string deliveryType, string channel, string status)
    {
        IntakeDeliveryAttempts
            .WithLabels(SanitizeLabel(deliveryType), SanitizeLabel(channel), SanitizeLabel(status))
            .Inc();
    }

    public void RecordCommunitySync(int importedCount, int rejectedCount)
    {
        if (importedCount > 0)
        {
            CommunityImports.WithLabels("imported").Inc(importedCount);
        }

        if (rejectedCount > 0)
        {
            CommunityImports.WithLabels("rejected").Inc(rejectedCount);
        }
    }

    public void RecordPeerSync(int blockedCount, int observedCount, int rejectedCount)
    {
        if (blockedCount > 0)
        {
            PeerImports.WithLabels("blocked").Inc(blockedCount);
        }

        if (observedCount > 0)
        {
            PeerImports.WithLabels("observed").Inc(observedCount);
        }

        if (rejectedCount > 0)
        {
            PeerImports.WithLabels("rejected").Inc(rejectedCount);
        }
    }

    private static string SanitizeLabel(string value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "unknown"
            : value.Trim().Replace(' ', '_').ToLowerInvariant();
    }
}
