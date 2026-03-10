# IIS Control Plane Refactor Plan

## Phase 1 - Analysis Findings

Current duplication candidates identified in IIS app:

- In-process C# middleware performed blocklist lookups and heuristic detection.
- Request-path tarpit rewrites were implemented directly in ASP.NET Core.
- App configuration was centered on Redis blocklist internals instead of Linux-engine integration.

These responsibilities overlap with Linux defense-engine execution and were marked for replacement by remote API orchestration.

## Phase 2 - Integration Interfaces

Introduced abstractions:

- `IDefenseEngineClient`
- `ITelemetryService`
- `IPolicyService`

Added adapter implementation:

- `LinuxDefenseEngineClient` with timeout/retry and fallback behavior.

## Phase 3 - Orchestration Refactor

- Removed direct Redis blocklist middleware path.
- Added control-plane endpoints for telemetry, policy dispatch, and escalation acknowledgement.
- Added `DefenseEngineSyncService` for periodic telemetry pull and policy sync.

## Phase 4 - Documentation Split

- Updated root architecture narrative to Linux-engine source-of-truth model.
- Added explicit control-plane boundary and provisional API contract documentation.


## Phase 5 - Operational hardening

- Made downstream endpoint routes configurable with `DefenseEngine:ApiRoutes` to avoid hardcoded Linux API paths.
- Made periodic background sync cadence configurable via `DefenseEngine:Sync:SyncIntervalSeconds`.
- Preserved existing control-plane auth filter behavior while reducing merge-conflict risk by keeping changes scoped to adapter configuration.
