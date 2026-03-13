# AI Scraping Defense (.NET)

This repository is being repositioned toward the original goal: a pure-.NET implementation of the `ai-scraping-defense` stack rather than an IIS-to-Linux control-plane adapter.

The current codebase now contains the first .NET-native defense slice inside [RedisBlocklistMiddlewareApp/Program.cs](RedisBlocklistMiddlewareApp/Program.cs):

- Redis-backed IP blocklist enforcement at the edge.
- Heuristic request inspection for known bad user agents, malformed headers, and suspicious paths.
- A bounded suspicious-request queue with a background analysis worker.
- Redis-backed request-frequency tracking for simple escalation decisions.
- Pluggable escalation via configured CIDR reputation ranges, optional HTTP reputation checks, and an optional OpenAI-compatible classifier hook.
- A deterministic tarpit endpoint that returns synthetic HTML and recursive links.
- A lightweight authenticated event feed at `/defense/events` for recent decisions.
- Authenticated operator metrics and blocklist management endpoints under `/defense/*`.
- An authenticated `/analyze` webhook endpoint with durable SQLite-backed intake for confirmed malicious events.

## Commercial v1 Scope

The first commercial release is a single deployable ASP.NET Core service that provides the core `ai-scraping-defense` workflow in .NET:

- inspect inbound requests at the edge
- tarpit suspicious traffic
- persist defense decisions and operator-visible events
- accept authenticated webhook intake for confirmed malicious traffic
- block and unblock IPs through authenticated operator endpoints

This is intentionally a modular monolith for v1. It preserves the upstream functional roles, but keeps them in one deployable until the .NET contracts and production behavior settle.

See [docs/architecture.md](docs/architecture.md) for the current architecture, [docs/commercial_scope.md](docs/commercial_scope.md) for the v1 definition, and [docs/dotnet_parity_roadmap.md](docs/dotnet_parity_roadmap.md) for the post-v1 parity queue.

## Implemented Endpoints

- `GET /health`
- `GET /anti-scrape-tarpit/{path}`

`GET /defense/events` is only exposed when `DefenseEngine:Management:ApiKey` is configured.
`GET /defense/metrics` and the blocklist management endpoints follow the same API-key protection via the configured `DefenseEngine:Management:ApiKeyHeaderName` header.
`POST /analyze` is only exposed when `DefenseEngine:Intake:ApiKey` is configured and expects the configured `DefenseEngine:Intake:ApiKeyHeaderName` header.

Management endpoints:
- `GET /defense/events?count=50`
- `GET /defense/metrics`
- `GET /defense/blocklist?ip=203.0.113.10`
- `POST /defense/blocklist?ip=203.0.113.10&reason=manual_block`
- `DELETE /defense/blocklist?ip=203.0.113.10`

Webhook intake endpoint:
- `POST /analyze`
  - body shape mirrors the legacy AI service webhook: `event_type`, `reason`, `timestamp_utc`, `details`
  - `details.ip` is required and must be a valid IP address
  - accepted events are written durably before background processing

## Supported Data Stores

- `Redis`: required for hot operational state such as blocklists and short-window frequency counters.
- `SQLite`: supported for local development, demos, and single-node/lightweight production installs as the durable audit and webhook intake store.
- `PostgreSQL`: planned as the primary production relational backend for richer tarpit content, sync features, and larger-scale persistence.
- `SQL Server`: deferred. It is not a commercial v1 target unless customer demand justifies the extra provider and test surface.

## Escalation Extensions

Queued suspicious-request analysis now persists a score breakdown with named contributions from:

- base edge heuristics
- short-window request frequency
- configured CIDR reputation ranges
- optional HTTP reputation providers
- an optional OpenAI-compatible classifier endpoint

The default configuration keeps the external reputation/model hooks disabled. They are exposed under `DefenseEngine:Escalation` so production deployments can opt in without changing the rest of the request pipeline.

## Configuration

The .NET defense foundation is configured in [RedisBlocklistMiddlewareApp/appsettings.json](RedisBlocklistMiddlewareApp/appsettings.json) under the `DefenseEngine` section.

Key areas:

- `DefenseEngine:Redis`
- `DefenseEngine:Heuristics`
- `DefenseEngine:Networking`
- `DefenseEngine:Management`
- `DefenseEngine:Intake`
- `DefenseEngine:Audit`
- `DefenseEngine:Escalation`
- `DefenseEngine:Queue`
- `DefenseEngine:Tarpit`

For direct edge deployments, leave `DefenseEngine:Networking:ClientIpResolutionMode` as `Direct`. If the app is behind a reverse proxy or CDN, switch it to `TrustedProxy` and populate `DefenseEngine:Networking:TrustedProxies` with the proxy IPs you explicitly trust.

Defense decisions are now persisted to SQLite via `DefenseEngine:Audit:DatabasePath`, which keeps recent event history available across restarts.

In `Production`, startup now fails fast if Redis still points at a loopback endpoint like `localhost` unless you explicitly opt in with `DefenseEngine:Redis:AllowLoopbackConnectionStringInProduction`.

## Status

The project now has an explicit commercial-v1 target, but release readiness still depends on closing the remaining items in [docs/release_blockers.md](docs/release_blockers.md) and the post-v1 parity queue in [docs/dotnet_parity_roadmap.md](docs/dotnet_parity_roadmap.md).
