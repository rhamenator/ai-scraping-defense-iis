# Commercial Scope

This document defines the first commercial release of the .NET stack. Its purpose is to stop treating the repository as an open-ended foundation and to make release readiness measurable.

## v1 Product Definition

Commercial v1 is a single deployable ASP.NET Core service with authenticated operator endpoints and durable local persistence.

Included in v1:

- Redis-backed IP blocklist enforcement
- request inspection for suspicious paths, malformed headers, query abuse, and known bad user agents
- rewrite of suspicious traffic into the tarpit surface
- asynchronous defense analysis with Redis-backed request frequency tracking
- authenticated operator endpoints for events, metrics, and manual blocklist actions
- authenticated `/analyze` intake for externally confirmed malicious events
- durable SQLite-backed audit and webhook inbox persistence
- automated unit and integration-style tests covering the core pipeline

Explicitly deferred from v1:

- multi-project service split into dedicated `EdgeGateway`, `EscalationEngine`, and `TarpitApi` processes
- peer sync and trust policy
- community blocklist sync
- richer reputation providers and optional LLM-backed scoring
- PostgreSQL-backed Markov tarpit content generation
- operator dashboard UI
- SQL Server support

## Supported Deployment Model

Commercial v1 supports these deployment modes:

- direct edge deployment where the app receives client IPs directly
- trusted-proxy deployment where explicit reverse proxy IPs are configured
- single-node durable deployment using Redis plus SQLite

Commercial v1 does not yet claim:

- multi-node relational consistency
- cross-instance signal sync
- operator UI parity with the upstream project

## Database Strategy

The storage model for commercial v1 is intentionally narrow:

- `Redis` is the required operational store for hot state.
- `SQLite` is the supported durable store for audit history and webhook intake in v1.
- `PostgreSQL` is the planned next production relational backend after v1.
- `SQL Server` is deferred until there is customer demand.

This keeps the provider surface small while still allowing a clean path to larger-scale persistence later.

## Release Criteria

Commercial v1 is release-ready when:

- the blocker queue in [release_blockers.md](release_blockers.md) is closed
- the shipped feature set matches the included v1 definition above
- all deferred items are documented as post-v1 work rather than implied release commitments
- `dotnet build` and `dotnet test` pass on the main solution
