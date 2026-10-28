# IIS Deployment Guide (.NET)

This guide covers deploying the current .NET stack to Windows Server IIS with ASP.NET Core Module (ANCM).

## Scope

- Host `RedisBlocklistMiddlewareApp` behind IIS.
- Use Redis for operational state.
- Optionally enable PostgreSQL-backed Markov tarpit content.
- Enable management/intake/peer endpoints only when API keys are configured.
- Keep single deployable mode as the default production topology.
- Support optional split-runtime topology when operational constraints justify it.

## Prerequisites

- Windows Server with IIS installed.
- ASP.NET Core Hosting Bundle matching the target runtime.
- URL Rewrite and ARR only if your topology needs additional proxying.
- Reachable Redis instance.
- Optional PostgreSQL instance initialized with `db/init_markov.sql`.

## 1) Publish the App

From repository root on a build host:

```powershell
dotnet restore
dotnet publish RedisBlocklistMiddlewareApp/RedisBlocklistMiddlewareApp.csproj -c Release -o .\deploy\RedisBlocklistMiddlewareApp
```

Copy `deploy\RedisBlocklistMiddlewareApp` to the target server.

## 2) Configure IIS Site and App Pool

1. Create an IIS Application Pool with:
   - `.NET CLR Version`: `No Managed Code`
   - Identity: `ApplicationPoolIdentity` (or dedicated service account)
2. Create an IIS Site (or Application) pointing to the published folder.
3. Ensure `web.config` from publish output is present (ANCM entrypoint).

## 3) Configure Application Settings

Set runtime configuration in `appsettings.json` and/or environment variables.

Minimum production settings:

- `ConnectionStrings:RedisConnection` (or `DefenseEngine:Redis:ConnectionString`)
- `DefenseEngine:Management:ApiKey`
- `DefenseEngine:Intake:ApiKey` (if intake webhook is required)

Optional but commonly used:

- `DefenseEngine:PeerSync:ExportApiKey`
- `DefenseEngine:Tarpit:PostgresMarkov:*`
- `DefenseEngine:Networking:ClientIpResolutionMode=TrustedProxy`
- `DefenseEngine:Networking:TrustedProxies` (when using proxy/CDN headers)

Optional split-runtime settings:

- `DefenseEngine:Topology:Mode=Split`
- `DefenseEngine:Services:EscalationEngine:BaseUrl`
- `DefenseEngine:Services:EscalationEngine:ApiKey`
- `DefenseEngine:Services:TarpitApi:BaseUrl`
- `DefenseEngine:Services:TarpitApi:ApiKey`

The release supports both `Single` and `Split`. In `Split`, configure the escalation and tarpit service URLs and service API keys, then verify both services are reachable from the edge host.

For distributed-attack operator alerts, configure one or more `DefenseEngine:Intake:Alerting` channels and `DefenseEngine:IncidentAlerts`. Set both `PrimaryOriginHealthUrl` and `BackupOriginHealthUrl` to enable a health-based manual failover choice. The monitor polls every 15 seconds by default and uses Redis to suppress duplicate alerts for 30 minutes. Its attack thresholds come from `DefenseEngine:Cloudflare` (`MinimumRecentDecisions`, `MinimumBlockedRate`, `MinimumDistinctBlockedOrigins`, and `AttackWindowMinutes`). Alerts and dashboard recommendations never change Cloudflare mode or origin routing automatically.

## 4) File-System Permissions

Grant the IIS app pool identity least-privilege access to:

- App folder: `Read & Execute`
- Data/audit path (for SQLite event store): `Modify`
- Any configured archive/output directories: `Modify`

Keep secrets outside source-controlled directories and inject them via environment or secure host-level secret storage.

## 5) Validation Checklist

After site start/recycle:

1. `GET /health` returns healthy.
2. `GET /` returns endpoint advertisement payload.
3. Tarpit route responds (default `/anti-scrape-tarpit/test`).
4. If management key configured, `GET /defense/events` works with API key header.
5. If intake key configured, `POST /analyze` accepts a valid payload.

## 6) Operational Hardening

- Run with HTTPS-only bindings.
- Restrict management and intake routes at network perimeter where possible.
- Use non-default API key header names in production.
- Set explicit trusted proxies if forwarded headers are enabled.
- Monitor `/health`, `/defense/metrics`, and Windows/IIS logs.

## 7) Split Runtime Validation (Optional)

For a split-runtime deployment:

1. Verify `EscalationEngine` and `TarpitApi` health endpoints are reachable from `EdgeGateway`.
2. Verify service-to-service API keys are required and validated.
3. Run an end-to-end suspicious request to confirm remote escalation and tarpit behavior.
4. Intentionally stop one downstream runtime and verify graceful degradation with observable error telemetry.

## Troubleshooting

- `500` at startup: verify Hosting Bundle/runtime mismatch and appsettings syntax.
- `503` from IIS: confirm app pool identity permissions and process startup logs.
- Redis health failures: verify connection string, TLS requirements, and firewall.
- Missing management/intake/peer routes: confirm related API keys are configured.

For endpoint contracts, see `api_references.md`. For topology contract details, see `runtime_topologies.md`. For release-time operational checks, see `operator_runbook.md` and `release_checklist.md`.
