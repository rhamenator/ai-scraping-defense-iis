<p align="center">
  <img src=".github/assets/logo.png" alt="AI Scraping Defense IIS logo" width="220">
</p>

# AI Scraping Defense (.NET)

This repository is being repositioned toward the original goal: a pure-.NET implementation of the `ai-scraping-defense` stack rather than an IIS-to-Linux control-plane adapter.

The solution is now split into role-oriented .NET projects while still shipping as a single deployable for v1:

- `AiScrapingDefense.EdgeGateway` for the ASP.NET Core host, middleware, operator API, webhook intake, and Redis-backed enforcement
- `AiScrapingDefense.Contracts` for shared options and cross-service models/interfaces
- `AiScrapingDefense.EscalationEngine` for threat scoring, reputation providers, and model adapters
- `AiScrapingDefense.TarpitApi` for tarpit page generation and PostgreSQL-backed Markov content

The current codebase now contains the first .NET-native defense slice inside [RedisBlocklistMiddlewareApp/Program.cs](RedisBlocklistMiddlewareApp/Program.cs):

- Redis-backed IP blocklist enforcement at the edge.
- Heuristic request inspection for known bad user agents, malformed headers, and suspicious paths.
- A bounded suspicious-request queue with a background analysis worker.
- Redis-backed request-frequency tracking for simple escalation decisions.
- Pluggable escalation via configured CIDR reputation ranges, an optional local trained model, optional HTTP reputation checks, and an optional OpenAI-compatible classifier hook.
- A deterministic tarpit endpoint that returns synthetic HTML and recursive links.
- A lightweight authenticated event feed at `/defense/events` for recent decisions.
- Authenticated operator metrics and blocklist management endpoints under `/defense/*`.
- A protected operator dashboard at `/defense/dashboard` backed by the same management API.
- An authenticated `/analyze` webhook endpoint with durable SQLite, PostgreSQL, or SQL Server-backed intake for confirmed malicious events.
- Configurable webhook, Slack, and SMTP alert dispatch for processed intake events.
- Configurable outbound community reporting for processed intake events.
- Optional community blocklist feed sync with authenticated status surfaced under `/defense/community-blocklist/status`.
- Optional peer sync with explicit `ObserveOnly` and `BlockList` trust modes plus authenticated signal export at `/peer-sync/signals`.
- Optional PostgreSQL-backed Markov tarpit content with deterministic render variants and rotating ZIP decoy archives.

## What's New Since v1.1.5

- **Loopback and unspecified addresses can no longer be blocked (`#263`, `#264`):** The blocklist now refuses `127.0.0.0/8`, `::1`, `0.0.0.0` and `::` (including IPv4-mapped forms), matching the Python and Rust editions. Private ranges remain blockable.
- **CodeQL hardening (`#262`):** Resolved path-combine and catch-all findings, including a narrower fail-open filter in the HTTP reputation provider.

## What's New Since v1.1.4

- **Authenticated `/defense/setup` Wizard (`#258`) & Edge Metrics Grid (`#257`):** Added a guided setup wizard for configuring the stack and an Edge metrics table grid in the operator dashboard.
- **Management & CSRF Security Hardening (`#243`, `#259`, `#260`):** Required HTTPS for the management API by default, returned `400 BadRequest` when `/defense/blocklist` omits the `ip` query parameter, and enforced same-origin checks on cookie-authenticated mutations.
- **Observability, Intake & Raft Bounding (`#242`, `#244`–`#255`):** Bound Prometheus and the OTLP collector to loopback, bounded webhook/intake/outbound response sizes and audit stores (SQLite, PostgreSQL, SQL Server), added per-credential intake rate limiting, and upgraded to DotNext 6 with Raft snapshots and compaction.
- **Credential & Release Hygiene (`#240`, `#241`, `#256`):** Kept credentials out of Docker build layers, delivery records, and HTTP client logs, and split installer build, signing, and release publishing workflows.

## Commercial v1 Scope

The first commercial release is a single deployable ASP.NET Core service that provides the core `ai-scraping-defense` workflow in .NET:

- inspect inbound requests at the edge
- tarpit suspicious traffic
- persist defense decisions and operator-visible events
- accept authenticated webhook intake for confirmed malicious traffic
- dispatch operator alerts and optional community reports from confirmed malicious intake events
- block and unblock IPs through authenticated operator endpoints

This is intentionally a single-deployable, multi-project .NET stack for v1. It preserves the upstream functional roles, but keeps them in one runtime deployment until separate service contracts and operational behavior settle.

See [docs/architecture.md](docs/architecture.md) for the current architecture, [docs/commercial_scope.md](docs/commercial_scope.md) for the v1 definition, and [docs/dotnet_parity_roadmap.md](docs/dotnet_parity_roadmap.md) for the post-v1 parity queue.
Operational release documentation now lives in [docs/parity_matrix.md](docs/parity_matrix.md), [docs/operator_runbook.md](docs/operator_runbook.md), and [docs/release_checklist.md](docs/release_checklist.md).
Release artifact policy now lives in [docs/release_artifacts.md](docs/release_artifacts.md).

## Implemented Endpoints

- `GET /health`
- `GET /anti-scrape-tarpit/{path}`

`GET /defense/dashboard` and the dashboard session endpoints are only exposed when `DefenseEngine:Management:ApiKey` is configured.
`GET /defense/events`, `GET /defense/metrics`, and the blocklist management endpoints follow the same management authentication. They accept the configured `DefenseEngine:Management:ApiKeyHeaderName` header or a dashboard session cookie created at `/defense/dashboard/session`.
`POST /analyze` is only exposed when `DefenseEngine:Intake:ApiKey` is configured and expects the configured `DefenseEngine:Intake:ApiKeyHeaderName` header.

Management endpoints:
- `GET /defense/dashboard`
- `GET /defense/dashboard/session`
- `POST /defense/dashboard/session`
- `DELETE /defense/dashboard/session`
- `GET /defense/events?count=50`
- `GET /defense/metrics`
- `GET /defense/intake-deliveries?count=50`
- `GET /defense/intake-delivery-metrics`
- `GET /defense/community-blocklist/status`
- `GET /defense/peer-sync/status`
- `GET /defense/blocklist?ip=203.0.113.10`
- `POST /defense/blocklist?ip=203.0.113.10&reason=manual_block`
- `DELETE /defense/blocklist?ip=203.0.113.10`

Webhook intake endpoint:
- `POST /analyze`
  - body shape mirrors the legacy AI service webhook: `event_type`, `reason`, `timestamp_utc`, `details`
  - `details.ip` is required and must be a valid IP address
  - accepted events are written durably before background processing

Peer export endpoint:
- `GET /peer-sync/signals?count=200`
  - only exposed when `DefenseEngine:PeerSync:ExportApiKey` is configured
  - requires the configured `DefenseEngine:PeerSync:ExportApiKeyHeaderName` header
  - exports recent blocked defense decisions as peer-shareable signals

## Supported Data Stores

- `Redis`: required for hot operational state such as blocklists and short-window frequency counters.
- `SQLite`: default for local development, demos, and single-node/lightweight production installs as the durable audit and webhook intake store.
- `PostgreSQL`: supported for production audit, webhook intake, and Markov-backed tarpit content.
- `SQL Server`: supported as an audit and webhook intake backend for Microsoft-heavy environments.

## Escalation Extensions

Queued suspicious-request analysis now persists a score breakdown with named contributions from:

- base edge heuristics
- short-window request frequency
- configured CIDR reputation ranges
- an optional local trained model
- optional HTTP reputation providers
- an optional OpenAI-compatible classifier endpoint

The default configuration keeps the external reputation/model hooks disabled. They are exposed under `DefenseEngine:Escalation` so production deployments can opt in without changing the rest of the request pipeline.
See [docs/local_model_training.md](docs/local_model_training.md) for the local model artifact format, dataset-builder inputs, and trainer CLI.

## PostgreSQL Tarpit

The tarpit can now switch from fixed synthetic paragraphs to a PostgreSQL-backed Markov corpus under `DefenseEngine:Tarpit:PostgresMarkov`. When enabled, the app reads `markov_words` and `markov_sequences` tables and uses the durable corpus to generate deterministic crawl-wasting paragraphs. The base schema is in [init_markov.sql](db/init_markov.sql).

## Configuration

The .NET defense foundation is configured in [RedisBlocklistMiddlewareApp/appsettings.json](RedisBlocklistMiddlewareApp/appsettings.json) under the `DefenseEngine` section.

Key areas:

- `DefenseEngine:Redis`
- `DefenseEngine:Heuristics`
- `DefenseEngine:Networking`
- `DefenseEngine:Cloudflare`
  - When enabled, the dashboard recommends considering Cloudflare Under Attack Mode only after the configured attack-evidence thresholds are met within `AttackWindowMinutes`. It does not make Cloudflare API changes automatically.
- `DefenseEngine:IncidentAlerts`
  - Polls recent decisions for a distributed attack and sends a cooldown-limited operator alert through configured generic webhook, Slack, and SMTP channels. Optional primary and backup health URLs add a manual failover choice when only the primary is unhealthy. Neither Cloudflare mode nor origin routing changes automatically.
- `DefenseEngine:Management`
  - `DashboardSessionHours` controls the browser dashboard session lifetime after successful sign-in.
  - `RequireSecureTransport` (default `true`) serves the management API and dashboard only over HTTPS. In Production the app refuses to start unless HTTPS is provided: an `https://` listener, a TLS-terminating proxy in `TrustedProxy` mode with the proxy listed in `Networking:TrustedProxies` (so its `X-Forwarded-Proto` is honoured), or IIS bindings (IIS owns the listener, so the startup check defers to it). A management request that did not arrive over HTTPS gets 403. The Development environment is exempt. For an HTTP-only test or staging deployment set it to `false`; Production then logs a warning at every start. `scripts/check_secure_transport_locally.sh` exercises all of this against the real app with a throwaway certificate.
- `DefenseEngine:Intake`
  - `Alerting:GenericWebhook` controls optional outbound webhook alerts for processed intake events.
  - `Alerting:Slack` controls optional Slack Incoming Webhook alerts for processed intake events.
  - `Alerting:Smtp` controls optional operator email alerts for processed intake events.
  - `CommunityReporting` controls optional outbound reporting to providers like AbuseIPDB.
- `DefenseEngine:Audit`
- `DefenseEngine:Escalation`
  - `LocalTrainedModel` enables the .NET-native local model adapter and points at the saved model artifact.
- `DefenseEngine:CommunityBlocklist`
- `DefenseEngine:PeerSync`
- `DefenseEngine:Queue`
- `DefenseEngine:Tarpit`
  - `MaxHops` defaults to `250` counted fetches for an already flagged IP; `0` disables the limit. The next hit returns 403 and stores a block reason with the existing `Redis:BlockDurationMinutes` TTL (default `1440`).
  - `HopWindowSeconds` defaults to `86400`; Redis expiry refreshes on each counted hit, so the count resets after this much inactivity. The edge and dedicated tarpit origin must use the same Redis connection and blocklist database in split topology. Redis errors fail open.
  - `FlaggedIpTtlSeconds` defaults to `604800` (seven days) and `RepeatOffenderThreshold` defaults to `3`, matching Python's tarpit flagger. Served hits refresh ordinary flag expiry; after more than three flags in the window, the flag has no expiry. In trusted-proxy mode, configure explicit trusted proxy addresses on the public tarpit origin.
  - `ArchiveDirectory`, `ArchiveRotationMinutes`, and `MaximumArchivesToKeep` control rotating ZIP decoy retention.
  - `JavaScriptDecoyFileCount`, `MinJavaScriptDecoyFileSizeKb`, and `MaxJavaScriptDecoyFileSizeKb` control generated JavaScript archive contents.
  - ZIP requests select one of at most four prebuilt archive variants per rotation. Generation runs outside the request path; an archive that is not ready, an overloaded reader, or an exceeded rate limit serves the ordinary tarpit HTML response. Archive fetches are capped at 30 per client and 1000 globally per minute, with at most 8 concurrent reads. Generated archives contain at most 32 JavaScript files of 64 KiB each, and reads reject archives over 4 MiB. These resource limits apply to the edge and dedicated tarpit origin independently.
- `DefenseEngine:Observability`

For direct edge deployments, leave `DefenseEngine:Networking:ClientIpResolutionMode` as `Direct`. If the app is behind a reverse proxy or CDN, switch it to `TrustedProxy`. Put ordinary TLS collectors/reverse proxies in `DefenseEngine:Networking:TrustedProxies` and CDN edges in the separate `TrustedCdnProxies` list.

For Cloudflare, populate `TrustedCdnProxies` with Cloudflare's published CIDR ranges and enable `DefenseEngine:Cloudflare`. Forwarded Headers middleware then restores the originating client IP before detection; configured proxy/CDN addresses are rejected by every blocklist insertion path. Cloudflare fingerprint headers are accepted only from `TrustedCdnProxies`, while collector headers are accepted only from ordinary `TrustedProxies`, preventing either trust boundary from falling through to the other. Cloudflare remains an ingress protection layer only—ordinary outbound model, webhook, feed, and backend traffic is sent directly to its configured destination so it does not add avoidable Cloudflare traffic cost.

Defense decisions and webhook intake records are persisted through `DefenseEngine:Audit`. The default provider is SQLite via `DatabasePath`; production deployments can set `Provider` to `Postgres` or `SqlServer` and provide `ConnectionString` instead.

In `Production`, startup now fails fast if Redis still points at a loopback endpoint like `localhost` unless you explicitly opt in with `DefenseEngine:Redis:AllowLoopbackConnectionStringInProduction`.

## Status

Commercial v1 is shipped and the blocker queue in [docs/release_blockers.md](docs/release_blockers.md) is closed. The remaining roadmap in [docs/dotnet_parity_roadmap.md](docs/dotnet_parity_roadmap.md) is post-v1 work rather than a release gate.

## Packaging

The repository now includes:

- a production-oriented multi-stage [Dockerfile](Dockerfile)
- a local smoke/deployment [compose.yaml](compose.yaml)
- an observability overlay at [compose.observability.yaml](compose.observability.yaml)
- a Windows service installer toolchain under [installer/](installer/) with build instructions in [docs/windows_installer.md](docs/windows_installer.md)
- a macOS packaging path under [installer/macos/](installer/macos/) with build instructions in [docs/macos_installer.md](docs/macos_installer.md)
- a GitHub Actions CI workflow at [.github/workflows/dotnet-ci.yml](.github/workflows/dotnet-ci.yml)
- a Windows installer workflow at [.github/workflows/windows-installer.yml](.github/workflows/windows-installer.yml)
- a macOS installer workflow at [.github/workflows/macos-installer.yml](.github/workflows/macos-installer.yml)
- a tagged-release image workflow at [.github/workflows/release-images.yml](.github/workflows/release-images.yml)

Run `python scripts/configure_credentials.py` to create `.env` through masked prompts (blank answers generate independent random values), then use `docker compose up --build` for a quick end-to-end environment with Redis and PostgreSQL. Compose fails before startup when a required secret is absent and points back to that setup command.
Use `docker compose -f compose.yaml -f compose.observability.yaml up --build` to include Prometheus, Grafana, and the OpenTelemetry Collector.
Use `./installer/Build-WindowsInstaller.ps1 -Version <semver>` on Windows to produce a `win-x64` installer, or pass `-Runtime win-arm64` for the ARM64 variant.
Use `./installer/macos/build-macos-packages.sh <semver>` on macOS to produce `.pkg` installers for `osx-x64` and `osx-arm64`.
The Windows installer registers the service by default and can optionally start it after valid production configuration is supplied.
Tagged release builds also attach Windows (`win-x64`, `win-arm64`) and macOS (`osx-x64`, `osx-arm64`) installer assets plus SHA-256 checksums to the GitHub Release.
Operator guidance for SmartScreen and Gatekeeper warnings is in [docs/download_warnings.md](docs/download_warnings.md).
Tagged releases publish signed GHCR images with provenance as documented in [docs/release_artifacts.md](docs/release_artifacts.md).
