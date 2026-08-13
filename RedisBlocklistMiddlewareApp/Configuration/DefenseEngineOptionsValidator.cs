using System.Net;
using Microsoft.Extensions.Options;

namespace RedisBlocklistMiddlewareApp.Configuration;

public sealed class DefenseEngineOptionsValidator : IValidateOptions<DefenseEngineOptions>
{
    public ValidateOptionsResult Validate(string? name, DefenseEngineOptions options)
    {
        var errors = new List<string>();
        var networking = options.Networking;

        if (!string.IsNullOrEmpty(options.TlsFingerprints.AttestationKey) &&
            options.TlsFingerprints.AttestationKey.Length < 32)
        {
            errors.Add("DefenseEngine:TlsFingerprints:AttestationKey must contain at least 32 characters when configured.");
        }
        if (!string.IsNullOrEmpty(options.TlsFingerprints.PreviousAttestationKey) &&
            options.TlsFingerprints.PreviousAttestationKey.Length < 32)
        {
            errors.Add("DefenseEngine:TlsFingerprints:PreviousAttestationKey must contain at least 32 characters when configured.");
        }
        if (options.TlsFingerprints.AttestationMaxAgeSeconds <= 0)
        {
            errors.Add("DefenseEngine:TlsFingerprints:AttestationMaxAgeSeconds must be positive.");
        }

        if (!string.Equals(options.Topology.Mode, RuntimeTopologyModes.Split, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(options.Topology.Mode, RuntimeTopologyModes.Single, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"DefenseEngine:Topology:Mode must be '{RuntimeTopologyModes.Single}' or '{RuntimeTopologyModes.Split}'.");
        }

        if (string.Equals(options.Topology.Mode, RuntimeTopologyModes.Split, StringComparison.OrdinalIgnoreCase))
        {
            if (!IsAbsoluteHttpUrl(options.Topology.EscalationBaseUrl))
            {
                errors.Add("DefenseEngine:Topology:EscalationBaseUrl must be an absolute HTTP(S) URL in Split mode.");
            }
            if (!IsAbsoluteHttpUrl(options.Topology.TarpitPublicBaseUrl))
            {
                errors.Add("DefenseEngine:Topology:TarpitPublicBaseUrl must be an absolute HTTP(S) URL in Split mode.");
            }
            if (string.IsNullOrWhiteSpace(options.Topology.ServiceToken) || options.Topology.ServiceToken.Length < 32)
            {
                errors.Add("DefenseEngine:Topology:ServiceToken must contain at least 32 characters in Split mode.");
            }
        }

        if (options.Consensus.Enabled)
        {
            if (!IPAddress.TryParse(options.Consensus.ListenAddress, out _))
            {
                errors.Add("DefenseEngine:Consensus:ListenAddress must be an IPv4 or IPv6 address.");
            }
            if (string.IsNullOrWhiteSpace(options.Consensus.AdvertisedHost) ||
                !Uri.CheckHostName(options.Consensus.AdvertisedHost.Trim('[', ']')).Equals(UriHostNameType.Dns) &&
                !IPAddress.TryParse(options.Consensus.AdvertisedHost.Trim('[', ']'), out _))
            {
                errors.Add("DefenseEngine:Consensus:AdvertisedHost must be a DNS host name or IP address.");
            }
            if (string.IsNullOrWhiteSpace(options.Consensus.StoragePath))
            {
                errors.Add("DefenseEngine:Consensus:StoragePath is required when consensus is enabled.");
            }
            if (string.IsNullOrWhiteSpace(options.Consensus.SharedSecret) || options.Consensus.SharedSecret.Length < 32)
            {
                errors.Add("DefenseEngine:Consensus:SharedSecret must contain at least 32 characters when consensus is enabled.");
            }
            if (options.Consensus.Members.Length != 1 &&
                (options.Consensus.Members.Length < 3 || options.Consensus.Members.Length % 2 == 0))
            {
                errors.Add("DefenseEngine:Consensus:Members must contain one development member or an odd production quorum of at least three members.");
            }

            var invalidRaftEndpoints = options.Consensus.Members
                .Where(member => !TryParseRaftEndpoint(member.RaftEndpoint, out _, out _))
                .Select(member => member.RaftEndpoint)
                .ToArray();
            if (invalidRaftEndpoints.Length > 0)
            {
                errors.Add($"DefenseEngine:Consensus:Members contains invalid Raft endpoints: {string.Join(", ", invalidRaftEndpoints)}.");
            }

            var invalidApiUrls = options.Consensus.Members
                .Where(member => !IsAbsoluteHttpUrl(member.ApiBaseUrl))
                .Select(member => member.ApiBaseUrl)
                .ToArray();
            if (invalidApiUrls.Length > 0)
            {
                errors.Add($"DefenseEngine:Consensus:Members contains invalid API base URLs: {string.Join(", ", invalidApiUrls)}.");
            }

            var advertisedEndpoint = FormatRaftEndpoint(
                options.Consensus.AdvertisedHost,
                options.Consensus.Port);
            if (!options.Consensus.Members.Any(member =>
                    string.Equals(member.RaftEndpoint, advertisedEndpoint, StringComparison.OrdinalIgnoreCase)))
            {
                errors.Add($"DefenseEngine:Consensus:Members must contain this node's advertised endpoint '{advertisedEndpoint}'.");
            }
        }

        if (!string.Equals(networking.ClientIpResolutionMode, ClientIpResolutionModes.Direct, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(networking.ClientIpResolutionMode, ClientIpResolutionModes.TrustedProxy, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"DefenseEngine:Networking:ClientIpResolutionMode must be '{ClientIpResolutionModes.Direct}' or '{ClientIpResolutionModes.TrustedProxy}'.");
        }

        var invalidTrustedProxies = networking.TrustedProxies
            .Where(proxy => !IsValidProxyOrNetwork(proxy))
            .ToArray();

        if (invalidTrustedProxies.Length > 0)
        {
            errors.Add(
                $"DefenseEngine:Networking:TrustedProxies contains invalid IP addresses or CIDR ranges: {string.Join(", ", invalidTrustedProxies)}.");
        }

        var invalidTrustedCdnProxies = networking.TrustedCdnProxies
            .Where(proxy => !IsValidProxyOrNetwork(proxy))
            .ToArray();

        if (invalidTrustedCdnProxies.Length > 0)
        {
            errors.Add(
                $"DefenseEngine:Networking:TrustedCdnProxies contains invalid IP addresses or CIDR ranges: {string.Join(", ", invalidTrustedCdnProxies)}.");
        }

        if (string.Equals(networking.ClientIpResolutionMode, ClientIpResolutionModes.TrustedProxy, StringComparison.OrdinalIgnoreCase) &&
            networking.TrustedProxies.Length == 0 &&
            networking.TrustedCdnProxies.Length == 0)
        {
            errors.Add(
                "DefenseEngine:Networking:TrustedProxies or TrustedCdnProxies must contain at least one IP address when ClientIpResolutionMode is 'TrustedProxy'.");
        }

        if (string.Equals(networking.ClientIpResolutionMode, ClientIpResolutionModes.Direct, StringComparison.OrdinalIgnoreCase) &&
            (networking.TrustedProxies.Length > 0 || networking.TrustedCdnProxies.Length > 0))
        {
            errors.Add(
                "DefenseEngine:Networking:TrustedProxies and TrustedCdnProxies must be empty when ClientIpResolutionMode is 'Direct'.");
        }

        if (IsEmptyEquivalentRoute(options.Tarpit.PathPrefix))
        {
            errors.Add(
                "DefenseEngine:Tarpit:PathPrefix must not resolve to the root path '/'.");
        }

        if (options.Observability.EnablePrometheusEndpoint &&
            IsEmptyEquivalentRoute(options.Observability.PrometheusEndpointPath))
        {
            errors.Add(
                "DefenseEngine:Observability:PrometheusEndpointPath must not resolve to the root path '/'.");
        }

        if (!string.Equals(options.Audit.Provider, AuditStorageProviders.Sqlite, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(options.Audit.Provider, AuditStorageProviders.Postgres, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(options.Audit.Provider, AuditStorageProviders.SqlServer, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"DefenseEngine:Audit:Provider must be '{AuditStorageProviders.Sqlite}', '{AuditStorageProviders.Postgres}', or '{AuditStorageProviders.SqlServer}'.");
        }

        if (!string.Equals(options.Audit.Provider, AuditStorageProviders.Sqlite, StringComparison.OrdinalIgnoreCase) &&
            string.IsNullOrWhiteSpace(options.Audit.ConnectionString))
        {
            errors.Add(
                "DefenseEngine:Audit:ConnectionString is required when DefenseEngine:Audit:Provider is 'Postgres' or 'SqlServer'.");
        }

        if (!string.Equals(options.Escalation.Routing.PreferredPrimaryRoute, ThreatModelRoutes.Auto, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(options.Escalation.Routing.PreferredPrimaryRoute, ThreatModelRoutes.Local, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(options.Escalation.Routing.PreferredPrimaryRoute, ThreatModelRoutes.Remote, StringComparison.OrdinalIgnoreCase))
        {
            errors.Add(
                $"DefenseEngine:Escalation:Routing:PreferredPrimaryRoute must be '{ThreatModelRoutes.Auto}', '{ThreatModelRoutes.Local}', or '{ThreatModelRoutes.Remote}'.");
        }

        if (options.Escalation.Containment.ChallengeScoreThreshold > options.Escalation.Containment.TarpitScoreThreshold ||
            options.Escalation.Containment.TarpitScoreThreshold > options.Escalation.Containment.ThrottleScoreThreshold ||
            options.Escalation.Containment.ThrottleScoreThreshold > options.Escalation.Containment.BlockScoreThreshold)
        {
            errors.Add(
                "DefenseEngine:Escalation:Containment thresholds must increase in this order: Challenge <= Tarpit <= Throttle <= Block.");
        }

        return errors.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(errors);
    }

    private static bool IsValidProxyOrNetwork(string value)
    {
        if (IPAddress.TryParse(value, out _))
        {
            return true;
        }

        var parts = value.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 ||
            !IPAddress.TryParse(parts[0], out var address) ||
            !int.TryParse(parts[1], out var prefixLength))
        {
            return false;
        }

        var maximumPrefix = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ? 32
            : 128;
        return prefixLength >= 0 && prefixLength <= maximumPrefix;
    }

    private static bool IsEmptyEquivalentRoute(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        return path.Trim().Trim('/').Length == 0;
    }

    private static bool IsAbsoluteHttpUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static bool TryParseRaftEndpoint(string? value, out string host, out int port)
    {
        host = string.Empty;
        port = 0;
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate($"tcp://{value.Trim()}", UriKind.Absolute, out var uri) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            uri.Port is < 1 or > 65535)
        {
            return false;
        }

        host = uri.Host;
        port = uri.Port;
        return true;
    }

    private static string FormatRaftEndpoint(string host, int port)
    {
        var normalizedHost = host.Trim().Trim('[', ']');
        return IPAddress.TryParse(normalizedHost, out var address) &&
            address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6
                ? $"[{address}]:{port}"
                : $"{normalizedHost}:{port}";
    }
}
