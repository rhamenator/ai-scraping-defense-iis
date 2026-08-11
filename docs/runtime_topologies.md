# Runtime Topologies

This document defines the supported single-runtime topology and the reserved contract for a future optional split deployment.

## Goals

- preserve the current single deployable mode as the default production path
- define clear runtime boundaries for `EdgeGateway`, `EscalationEngine`, and `TarpitApi`
- provide configuration contracts that allow operators to split runtimes incrementally
- define validation checks for both single-node and split deployments

## Runtime Modes

### Mode A: Single Deployable (default)

- One ASP.NET Core deployment hosts the full defense pipeline.
- Internal service calls remain in-process.
- Redis remains required for hot operational state.
- SQLite remains the default durable event store, with PostgreSQL and SQL Server available through `DefenseEngine:Audit:Provider`.

This mode is the commercial v1 baseline and remains supported after split-runtime enablement.

### Mode B: Optional Split Runtime (reserved, not implemented)

- `EdgeGateway` runs as the ingress-facing process.
- `EscalationEngine` runs as a separate process with provider/model dependencies.
- `TarpitApi` runs as a separate process focused on tarpit response generation.
- Contracts between runtimes are authenticated service-to-service HTTP calls.

The project boundaries exist, but they are class libraries inside the single deployable; they are not independently executable services yet. Setting `DefenseEngine__Topology__Mode=Split` is rejected during startup so the setting cannot fail silently while the monolith continues running. The remaining sections define the implementation contract, not a currently supported deployment.

## Runtime Boundaries

### EdgeGateway boundary

- accepts inbound traffic and applies request-inspection policy
- performs blocklist checks and tarpit/allow routing decisions
- forwards analysis payloads to `EscalationEngine` when split mode is enabled
- calls `TarpitApi` for tarpit response generation when split mode is enabled

### EscalationEngine boundary

- accepts authenticated analysis intake from `EdgeGateway`
- applies scoring, enrichment, and optional model/provider adapters
- writes audit and webhook inbox records through shared persistence policy
- emits operator-visible events/metrics

### TarpitApi boundary

- accepts authenticated tarpit render requests from `EdgeGateway`
- generates deterministic or Markov-backed tarpit responses
- enforces rendering/time budgets to avoid control-plane contention

## Configuration Contract

The future split implementation is expected to use explicit service endpoints and service keys:

- `DefenseEngine__Topology__Mode=Split`
- `DefenseEngine__Services__EscalationEngine__BaseUrl`
- `DefenseEngine__Services__EscalationEngine__ApiKey`
- `DefenseEngine__Services__TarpitApi__BaseUrl`
- `DefenseEngine__Services__TarpitApi__ApiKey`

Today, use `DefenseEngine__Topology__Mode=Single` (or omit it). `Split`, unknown values, and misspellings fail startup validation.

## Validation Checklist

### Single Deployable validation

1. `GET /health` reports healthy.
2. `GET /` returns endpoint advertisement payload.
3. `POST /analyze` works with configured intake key.
4. Tarpit route responds and logs expected metadata.

### Future split-runtime acceptance criteria

1. `EdgeGateway` health endpoint is healthy.
2. `EscalationEngine` health endpoint is healthy and rejects missing/invalid service key.
3. `TarpitApi` health endpoint is healthy and rejects missing/invalid service key.
4. End-to-end suspicious request flow reaches escalation and tarpit via remote calls.
5. Failure of one downstream runtime degrades safely (deny-by-policy or bounded fallback) and is visible in metrics/logs.

## Operator Guidance

- Start with Mode A unless a clear isolation/scaling requirement exists.
- Move to Mode B one boundary at a time (`EscalationEngine` first, then `TarpitApi`).
- Keep the same API-key hygiene and trusted-proxy controls used in single-node mode.
- Run release-checklist validation after each topology transition.
