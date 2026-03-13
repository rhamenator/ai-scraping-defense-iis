# AI Scraping Defense (.NET Foundation)

This repository is being repositioned toward the original goal: a pure-.NET implementation of the `ai-scraping-defense` stack rather than an IIS-to-Linux control-plane adapter.

The current codebase now contains the first .NET-native defense slice inside [RedisBlocklistMiddlewareApp/Program.cs](RedisBlocklistMiddlewareApp/Program.cs):

- Redis-backed IP blocklist enforcement at the edge.
- Heuristic request inspection for known bad user agents, malformed headers, and suspicious paths.
- A bounded suspicious-request queue with a background analysis worker.
- Redis-backed request-frequency tracking for simple escalation decisions.
- A deterministic tarpit endpoint that returns synthetic HTML and recursive links.
- A lightweight authenticated event feed at `/defense/events` for recent decisions.

## Current Scope

Today the repository is still a hybrid of legacy Python-era assets and the new .NET defense app. The .NET app is the active direction. It intentionally mirrors the same functional roles as the upstream Python project, but starts as a modular ASP.NET Core service instead of a multi-container Python stack.

See [docs/architecture.md](docs/architecture.md) for the current architecture and [docs/dotnet_parity_roadmap.md](docs/dotnet_parity_roadmap.md) for the parity plan against the upstream repository.

## Implemented Endpoints

- `GET /health`
- `GET /anti-scrape-tarpit/{path}`

`GET /defense/events` is only exposed when `DefenseEngine:Management:ApiKey` is configured.

## Near-Term Roadmap

- Split the current modular monolith into clear .NET service boundaries that line up with the upstream roles: edge gateway, AI intake, escalation engine, tarpit API, and admin surface.
- Replace the current synthetic tarpit page generator with a PostgreSQL-backed Markov or equivalent .NET content engine.
- Add persistent operational telemetry and blocklist management endpoints.
- Port community blocklist sync, peer sync, and model-based escalation into .NET services.

## Configuration

The .NET defense foundation is configured in [RedisBlocklistMiddlewareApp/appsettings.json](RedisBlocklistMiddlewareApp/appsettings.json) under the `DefenseEngine` section.

Key areas:

- `DefenseEngine:Redis`
- `DefenseEngine:Heuristics`
- `DefenseEngine:Networking`
- `DefenseEngine:Management`
- `DefenseEngine:Queue`
- `DefenseEngine:Tarpit`

For direct edge deployments, leave `DefenseEngine:Networking:ClientIpResolutionMode` as `Direct`. If the app is behind a reverse proxy or CDN, switch it to `TrustedProxy` and populate `DefenseEngine:Networking:TrustedProxies` with the proxy IPs you explicitly trust.

## Status

This remains work in progress. The solution now builds with `dotnet`, but release readiness still depends on completing the blocker queue in [docs/release_blockers.md](docs/release_blockers.md).
