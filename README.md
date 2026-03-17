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
- An authenticated `/analyze` webhook endpoint with durable SQLite-backed intake for confirmed malicious events.
- Configurable webhook, Slack, and SMTP alert dispatch for processed intake events.
- Configurable outbound community reporting for processed intake events.
- Optional community blocklist feed sync with authenticated status surfaced under `/defense/community-blocklist/status`.
- Optional peer sync with explicit `ObserveOnly` and `BlockList` trust modes plus authenticated signal export at `/peer-sync/signals`.
- Optional PostgreSQL-backed Markov tarpit content with deterministic render variants and rotating ZIP decoy archives.

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
- `SQLite`: supported for local development, demos, and single-node/lightweight production installs as the durable audit and webhook intake store.
- `PostgreSQL`: supported for the Markov-backed tarpit corpus and remains the primary production relational direction for richer content and larger-scale persistence.
- `SQL Server`: deferred. It is not a commercial v1 target unless customer demand justifies the extra provider and test surface.

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

The tarpit can now switch from fixed synthetic paragraphs to a PostgreSQL-backed Markov corpus under `DefenseEngine:Tarpit:PostgresMarkov`. When enabled, the app reads `markov_words` and `markov_sequences` tables and uses the durable corpus to generate deterministic crawl-wasting paragraphs. The base schema is in [init_markov.sql](/home/rich/dev/ai-scraping-defense-iis/db/init_markov.sql).

## Configuration

The .NET defense foundation is configured in [RedisBlocklistMiddlewareApp/appsettings.json](RedisBlocklistMiddlewareApp/appsettings.json) under the `DefenseEngine` section.

Key areas:

- `DefenseEngine:Redis`
- `DefenseEngine:Heuristics`
- `DefenseEngine:Networking`
- `DefenseEngine:Management`
  - `DashboardSessionHours` controls the browser dashboard session lifetime after successful sign-in.
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
  - `ArchiveDirectory`, `ArchiveRotationMinutes`, and `MaximumArchivesToKeep` control rotating ZIP decoy retention.
  - `JavaScriptDecoyFileCount`, `MinJavaScriptDecoyFileSizeKb`, and `MaxJavaScriptDecoyFileSizeKb` control generated JavaScript archive contents.
- `DefenseEngine:Observability`

For direct edge deployments, leave `DefenseEngine:Networking:ClientIpResolutionMode` as `Direct`. If the app is behind a reverse proxy or CDN, switch it to `TrustedProxy` and populate `DefenseEngine:Networking:TrustedProxies` with the proxy IPs you explicitly trust.

Defense decisions are now persisted to SQLite via `DefenseEngine:Audit:DatabasePath`, which keeps recent event history available across restarts.

In `Production`, startup now fails fast if Redis still points at a loopback endpoint like `localhost` unless you explicitly opt in with `DefenseEngine:Redis:AllowLoopbackConnectionStringInProduction`.

## Status

The project now has an explicit commercial-v1 target, but release readiness still depends on closing the remaining items in [docs/release_blockers.md](docs/release_blockers.md) and the post-v1 parity queue in [docs/dotnet_parity_roadmap.md](docs/dotnet_parity_roadmap.md).

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

Use `docker compose up --build` for a quick end-to-end environment with Redis and PostgreSQL.
Use `docker compose -f compose.yaml -f compose.observability.yaml up --build` to include Prometheus, Grafana, and the OpenTelemetry Collector.
Use `./installer/Build-WindowsInstaller.ps1 -Version <semver>` on Windows to produce an Inno Setup installer for the .NET runtime.
Use `./installer/macos/build-macos-packages.sh <semver>` on macOS to produce `.pkg` installers for `osx-x64` and `osx-arm64`.
The Windows installer registers the service by default and can optionally start it after valid production configuration is supplied.
Tagged release builds also attach Windows and macOS installer assets plus SHA-256 checksums to the GitHub Release.
Operator guidance for SmartScreen and Gatekeeper warnings is in [docs/download_warnings.md](docs/download_warnings.md).
Tagged releases publish signed GHCR images with provenance as documented in [docs/release_artifacts.md](docs/release_artifacts.md).
