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
| Webhook intake for confirmed malicious traffic | Implemented | `/analyze` is authenticated, durable, processed asynchronously, and can trigger outbound alert/report workflows. | Keep expanding provider coverage only when operational demand justifies it. |
| Community blocklist sync | Implemented | Feed sync, status reporting, import tracking, and outbound community reporting for intake events are in place. | Add richer trust policies and provider-specific controls as needed. |
| Peer sync | Implemented | Timed imports, authenticated exports, and `ObserveOnly`/`BlockList` trust modes are implemented. | Add richer trust scoring and coordination. |
| PostgreSQL-backed Markov tarpit | Implemented | The tarpit can load a Markov corpus from PostgreSQL and falls back safely when no snapshot exists. | Expand deeper decoy modes. See issue `#55`. |
| Advanced tarpit decoys | Implemented | The tarpit now serves rotating ZIP decoy archives with deterministic fake JavaScript payloads in addition to deterministic HTML modes. | Expand artifact families only when they add measurable crawler cost. |
| Reputation providers and classifier hooks | Implemented | Configured ranges, HTTP reputation, .NET-native local trained models, and OpenAI-compatible model adapters exist. | Add richer dataset-building and retraining ergonomics. See issue `#64`. |
| Alerting and operator/community reporting | Implemented | Confirmed malicious intake events can dispatch generic webhook alerts, Slack Incoming Webhook alerts, SMTP alerts, and configurable community reports with durable delivery visibility. | Add provider-specific enrichments only when operators show real demand. |
| Structured telemetry export | Implemented | Prometheus metrics, OTLP trace export, packaged scrape/alert config, and a bundled Grafana dashboard are included. | Tune thresholds and dashboard panels against production traffic after deployment. |
| Independent multi-service deployment | Deferred | v1 intentionally ships as a single deployable ASP.NET Core runtime. | Split into independently deployed roles only when operations justify it. |
| SQL Server support | Deferred | Redis + SQLite + PostgreSQL are the supported data stores for v1. | Revisit only if customer demand justifies the extra provider surface. |

Commercial v1 is feature-complete enough to package and validate, but fuller upstream parity still depends on closing issue `#64`.
