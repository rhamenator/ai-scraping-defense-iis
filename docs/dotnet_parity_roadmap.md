# .NET Parity Roadmap

This document maps the upstream `ai-scraping-defense` roles to the .NET implementation in this repository.

Commercial v1 is defined in [commercial_scope.md](commercial_scope.md). This roadmap now tracks post-v1 parity work instead of acting as an implied release checklist.

The issue-backed implementation sequence is broken into explicit tracks in [agentic_core_parity_tracks.md](agentic_core_parity_tracks.md) so contributors can pick up non-overlapping workstreams before broader agentic-core changes begin.

## Upstream Role Mapping

| Upstream role | Current .NET status | Next target |
| --- | --- | --- |
| Nginx/Lua edge filter | Implemented as ASP.NET Core middleware in the `AiScrapingDefense.EdgeGateway` host | Separate runtime deployment only if production topology justifies it |
| AI Service webhook | Implemented as authenticated `/analyze` plus durable SQLite-backed intake | Add richer alerting/reporting and optional isolated runtime boundary |
| Escalation Engine | Implemented in its own solution project with baseline scoring, reputation-provider hooks, and an optional OpenAI-compatible model adapter | Add more provider types, richer telemetry, and optional isolated runtime boundary |
| Tarpit API | Implemented as deterministic page generation with PostgreSQL-backed Markov support and multiple render variants | Add streaming/archive rotation parity and deeper crawl-wasting content sources |
| Admin UI | Implemented as the protected `/defense/dashboard` operator console | Add richer workflows, search, and operator ergonomics |
| Community blocklist sync | Implemented as a configurable feed sync worker with admin-visible status | Add richer source trust, reporting parity, and deduplication policies |
| Peer sync | Implemented with authenticated signal export, timed import, and explicit trust modes | Add richer trust scoring, deduplication policy, and multi-node coordination |
| Metrics/observability | Implemented at operator-API level | Add structured metrics, traces, and richer telemetry export |

## Post-v1 Parity Queue

1. Promote the project-level split into optional independent runtime deployments when production constraints justify it (see [runtime_topologies.md](runtime_topologies.md)).
2. Add richer escalation scoring, reputation providers, and optional LLM adapters (see [escalation_scoring_provider_baseline.md](escalation_scoring_provider_baseline.md)).
3. Expand the tarpit content strategy with streaming/archive rotation and deeper content sources (see [tarpit_content_strategy_baseline.md](tarpit_content_strategy_baseline.md)).
4. Add richer community-blocklist trust policy, reporting, and peer coordination (see [community_blocklist_peer_coordination_baseline.md](community_blocklist_peer_coordination_baseline.md)).
5. Add structured metrics, traces, and richer operator telemetry export (see [observability_telemetry_export_baseline.md](observability_telemetry_export_baseline.md)).
6. Close remaining operator workflow and UX parity gaps (see [operator_ui_workflow_parity_baseline.md](operator_ui_workflow_parity_baseline.md)).

## Implementation Tracks

Use [agentic_core_parity_tracks.md](agentic_core_parity_tracks.md) as the execution plan for how the parity backlog should be staged. In short:

- Track A stabilizes orchestration, routing, and containment.
- Track B adds extensibility and decision-memory surfaces.
- Track C layers operator recommendations and explainability on top of those stable seams.

Those tracks should land before broader "more agentic" orchestration work begins.
