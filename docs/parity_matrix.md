# .NET Parity Matrix

This matrix compares the current .NET stack to the functional roles in the upstream `ai-scraping-defense` project. The goal is to separate "commercial v1 ready" from "full upstream parity still pending."

Status legend:

- `Implemented`: shipped in the current .NET runtime
- `Partial`: usable, but still missing important upstream behaviors or operator depth
- `Deferred`: intentionally left out of commercial v1

| Capability | .NET status | Notes | Follow-up |
| --- | --- | --- | --- |
| Edge request inspection and Redis blocklisting | Implemented | ASP.NET Core middleware handles heuristics, Redis blocklist checks, and queued escalation. | Keep hardening detection quality through additional reputation/model providers. |
| Suspicious-request queue and background analysis | Implemented | Requests are queued, scored, persisted, and can trigger auto-blocking. | Expand scoring inputs and tuning ergonomics. |
| Authenticated operator API | Implemented | `/defense/events`, `/defense/metrics`, blocklist controls, community status, and peer-sync status are protected. | Add richer operator workflows and filtering. |
| Operator dashboard | Implemented | Browser dashboard ships inside the same deployable and uses the same protected management API. | Expand search, drill-down, and investigation ergonomics. |
| Webhook intake for confirmed malicious traffic | Implemented | `/analyze` is authenticated, durable, and processed asynchronously. | Add alerting/reporting parity from the legacy AI-service path. See issue `#53`. |
| Community blocklist sync | Implemented | Feed sync, status reporting, and import tracking are in place. | Add richer trust policies and reporting controls. See issue `#53`. |
| Peer sync | Implemented | Timed imports, authenticated exports, and `ObserveOnly`/`BlockList` trust modes are implemented. | Add richer trust scoring and coordination. |
| PostgreSQL-backed Markov tarpit | Implemented | The tarpit can load a Markov corpus from PostgreSQL and falls back safely when no snapshot exists. | Expand deeper decoy modes. See issue `#55`. |
| Advanced tarpit decoys | Partial | Current tarpit modes cover deterministic HTML, archive, and API-catalog variants. | Port rotating archives and JavaScript ZIP honeypots. See issue `#55`. |
| Reputation providers and classifier hooks | Partial | Configured ranges, HTTP reputation, and OpenAI-compatible model adapters exist. | Port trained ML lifecycle and richer provider orchestration. See issue `#54`. |
| Alerting and operator/community reporting | Partial | Durable intake exists, but outbound alert/report workflows from the legacy AI service are not fully ported. | See issue `#53`. |
| Structured telemetry export | Partial | Prometheus metrics and OTLP trace export are wired into the app. | Add dashboard examples, alert rules, and richer release telemetry guidance. |
| Independent multi-service deployment | Deferred | v1 intentionally ships as a single deployable ASP.NET Core runtime. | Split into independently deployed roles only when operations justify it. |
| SQL Server support | Deferred | Redis + SQLite + PostgreSQL are the supported data stores for v1. | Revisit only if customer demand justifies the extra provider surface. |

Commercial v1 is feature-complete enough to package and validate, but full upstream parity still depends on closing issues `#53`, `#54`, and `#55`.

