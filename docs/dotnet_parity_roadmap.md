# .NET Parity Roadmap

This document maps the upstream `ai-scraping-defense` roles to the .NET implementation in this repository.

## Upstream Role Mapping

| Upstream role | Current .NET status | Next target |
| --- | --- | --- |
| Nginx/Lua edge filter | Implemented as ASP.NET Core middleware | Split into a dedicated edge gateway project |
| AI Service webhook | Partially represented by queued suspicious-request intake | Add explicit webhook/API surface and durable intake storage |
| Escalation Engine | Partially represented by `DefenseAnalysisService` | Add richer heuristics, persistent telemetry, model scoring, optional LLM adapters |
| Tarpit API | Implemented as deterministic synthetic page endpoint | Add richer tarpit modes, streaming, and Markov/PostgreSQL-backed content |
| Admin UI | Not implemented in .NET | Add admin API first, then UI |
| Community blocklist sync | Not implemented in .NET | Add background worker and contracts |
| Peer sync | Not implemented in .NET | Add background worker and peer trust model |
| Metrics/observability | Limited to recent in-memory event feed | Add structured metrics, health, traces, and persistent audit events |

## What Was Completed In This Step

- Removed the Linux control-plane adapter branch from `main`.
- Re-established the repo around the original pure-.NET objective.
- Reworked the ASP.NET Core app into a .NET-native foundation with:
  - Redis blocklist service
  - suspicious-request queue
  - background escalation worker
  - frequency tracking
  - tarpit endpoint
  - recent decision feed

## Recommended Next Implementation Steps

1. Split the current app into `EdgeGateway`, `EscalationEngine`, and `TarpitApi` projects under the solution while keeping shared contracts in a common library.
2. Add a durable decision/event store so the current `/defense/events` feed survives restarts.
3. Port the upstream tarpit content generation strategy into a .NET service backed by PostgreSQL.
4. Add model-adapter and reputation-provider interfaces so richer analysis can be plugged in without reworking the pipeline again.
