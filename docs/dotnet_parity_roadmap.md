# .NET Parity Roadmap

This document maps the upstream `ai-scraping-defense` roles to the .NET implementation in this repository.

Commercial v1 is defined in [commercial_scope.md](commercial_scope.md). This roadmap now tracks post-v1 parity work instead of acting as an implied release checklist.

## Upstream Role Mapping

| Upstream role | Current .NET status | Next target |
| --- | --- | --- |
| Nginx/Lua edge filter | Implemented as ASP.NET Core middleware | Split into a dedicated edge gateway project |
| AI Service webhook | Implemented as authenticated `/analyze` plus durable SQLite-backed intake | Add richer alerting/reporting and separate service boundary |
| Escalation Engine | Implemented with baseline scoring, reputation-provider hooks, and an optional OpenAI-compatible model adapter | Add more provider types, richer telemetry, and separate service boundary |
| Tarpit API | Implemented as deterministic synthetic page endpoint | Add richer tarpit modes, streaming, and Markov/PostgreSQL-backed content |
| Admin UI | Not implemented in .NET | Add operator dashboard on top of the authenticated admin API |
| Community blocklist sync | Implemented as a configurable feed sync worker with admin-visible status | Add richer source trust, reporting parity, and deduplication policies |
| Peer sync | Implemented with authenticated signal export, timed import, and explicit trust modes | Add richer trust scoring, deduplication policy, and multi-node coordination |
| Metrics/observability | Implemented at operator-API level | Add structured metrics, traces, and richer telemetry export |

## Post-v1 Parity Queue

1. Split the current app into `EdgeGateway`, `EscalationEngine`, and `TarpitApi` projects under the solution while keeping shared contracts in a common library.
2. Add richer escalation scoring, reputation providers, and optional LLM adapters.
3. Port the upstream tarpit content generation strategy into a .NET service backed by PostgreSQL.
4. Add community blocklist sync and peer sync.
5. Build an operator dashboard UI on top of the authenticated admin API.
