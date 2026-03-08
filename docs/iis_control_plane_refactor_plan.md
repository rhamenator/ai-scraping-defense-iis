# IIS Control Plane Refactor Plan

## Phase 1 - Analysis Findings

Current duplication candidates identified in IIS app:

- In-process C# middleware performed blocklist lookups and heuristic detection.
- Request-path tarpit rewrites were implemented directly in ASP.NET Core.
- App configuration was centered on Redis blocklist internals instead of Linux-engine integration.

These responsibilities overlap with Linux defense-engine execution and were replaced by remote API orchestration.

## Phase 2 - Integration Interfaces

Introduced abstractions:

- `IDefenseEngineClient`
- `ITelemetryService`
- `IPolicyService`

Added adapter implementation:

- `LinuxDefenseEngineClient` with timeout/retry/cancellation-aware fallback behavior.

## Phase 3 - Orchestration Refactor

- Removed direct Redis blocklist middleware path.
- Added control-plane endpoints for telemetry, policy dispatch, escalation acknowledgement, and downstream health.
- Added `DefenseEngineSyncService` for periodic telemetry pull and policy sync.

## Phase 4 - Hardening and Operations

- Added startup option validation (`DefenseEngineOptionsValidator`).
- Added configurable downstream API routes (`DefenseEngine:ApiRoutes`) to avoid hardcoded paths.
- Added configurable background sync interval (`DefenseEngine:Sync`).
- Added Windows deployment helper script and Windows Event Log integration hook.

## Phase 5 - Documentation Split

- Updated root architecture narrative to Linux-engine source-of-truth model.
- Added explicit control-plane boundary and provisional API contract documentation.
