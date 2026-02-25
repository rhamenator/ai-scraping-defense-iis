# IIS Control Plane + Linux Defense Engine Architecture

This document describes the post-refactor split between the two related repositories:

- **Execution engine (Linux, source of truth):** `ai-scraping-defense`
- **Enterprise control plane (Windows/IIS):** `ai-scraping-defense-iis` (this repository)

## Design Principle

The IIS project must **integrate with** the Linux defense stack, not recreate it.

### Explicit Non-Goals for IIS

- No Lua/NGINX detection logic port
- No duplicate bot-classification pipeline in C#
- No forked tarpit or escalation algorithm

## Layered Model

```mermaid
flowchart TD
    subgraph Windows[Windows / IIS Control Plane]
        UI[Admin UI + Policy Workflows]
        API[ASP.NET Core Control APIs]
        BG[DefenseEngineSyncService]
    end

    subgraph Boundary[Integration Boundary]
        CL[LinuxDefenseEngineClient]
        IF1[IDefenseEngineClient]
        IF2[ITelemetryService]
        IF3[IPolicyService]
    end

    subgraph Linux[Linux Defense Engine - Remote Appliance]
        NGINX[NGINX + Lua]
        TARPIT[Tarpits + Honeypots]
        ESC[Escalation Engine]
        TEL[Telemetry Generation]
    end

    UI --> API
    API --> IF1
    API --> IF2
    API --> IF3
    BG --> IF2
    BG --> IF3

    IF1 --> CL
    IF2 --> CL
    IF3 --> CL

    CL --> NGINX
    CL --> TARPIT
    CL --> ESC
    CL --> TEL
```

## IIS Control Plane Components

- **`IDefenseEngineClient`**: abstraction for health, telemetry retrieval, policy submission, and escalation acknowledgment.
- **`LinuxDefenseEngineClient`**: HTTP adapter with timeout/retry and fallback behavior.
- **`ITelemetryService` / `TelemetryService`**: orchestration-level telemetry cache and refresh.
- **`IPolicyService` / `PolicyService`**: policy push workflow and deferred queue synchronization.
- **`DefenseEngineSyncService`**: periodic pull/push background synchronization.

## Provisional Linux API Contract

Until Linux APIs are formally versioned, the IIS adapter targets:

- `GET /health`
- `GET /api/v1/telemetry`
- `POST /api/v1/policies`
- `POST /api/v1/escalations/ack`

If endpoints differ, update only the adapter/client layer while preserving IIS control-plane interfaces.
