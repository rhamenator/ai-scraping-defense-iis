# Commercial Scope

This document defines the first commercial release of the .NET stack. Its purpose is to stop treating the repository as an open-ended foundation and to make release readiness measurable.

## v1 Product Definition

Commercial v1 is a single deployable, multi-project ASP.NET Core solution with authenticated operator endpoints and durable local persistence.

Included in v1:

- Redis-backed IP blocklist enforcement
- request inspection for suspicious paths, malformed headers, query abuse, and known bad user agents
- rewrite of suspicious traffic into the tarpit surface
- asynchronous defense analysis with Redis-backed request frequency tracking
- authenticated operator endpoints for events, metrics, and manual blocklist actions
- protected operator dashboard on top of the authenticated management API
- authenticated `/analyze` intake for externally confirmed malicious events
- configurable alert dispatch and outbound community reporting for processed intake events
- durable SQLite-backed audit and webhook inbox persistence
- configurable community blocklist sync with operator-visible status
- peer sync with authenticated export and explicit trust modes
- PostgreSQL-backed Markov tarpit content and deterministic render variants
- explicit solution boundaries for `EdgeGateway`, `EscalationEngine`, `TarpitApi`, and shared contracts
- automated unit and integration-style tests covering the core pipeline

Explicitly deferred from v1:

- separate runtime deployments for `EdgeGateway`, `EscalationEngine`, and `TarpitApi`
- SQL Server support

Deferred split-runtime work is tracked in [runtime_topologies.md](runtime_topologies.md).

## Supported Deployment Model

Commercial v1 supports these deployment modes:

- direct edge deployment where the app receives client IPs directly
- trusted-proxy deployment where explicit reverse proxy IPs are configured
- single-node durable deployment using Redis plus SQLite

Commercial v1 does not yet claim:

- multi-node relational consistency
- full-fidelity multi-node coordination beyond the shipped peer-sync exchange model
- complete operator UI parity with the upstream project
- independent runtime deployment as the default topology (single deployable remains the supported baseline)

Post-v1 multi-node durability and coordination work is tracked in [multi_node_durability_coordination_baseline.md](multi_node_durability_coordination_baseline.md).

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
