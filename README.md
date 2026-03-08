# AI Scraping Defense (IIS Control Plane Adapter)

This repository is the **Windows enterprise integration layer** for the Linux-native [ai-scraping-defense](https://github.com/rhamenator/ai-scraping-defense) defense engine.

## Architecture Positioning

This IIS/.NET project is intentionally scoped as a:

- **Control Plane / Management Layer**
- **Enterprise Integration Surface**
- **Windows-native Deployment Adapter**

It does **not** reimplement Linux defense execution behavior (Lua detection, tarpit internals, honeypot engines, escalation pipelines, or network-layer controls).

## Responsibility Split

### Linux Defense Engine (Source of Truth)

Owns defensive execution:

- Detection logic
- Honeypots / tarpits
- Escalation engine
- Telemetry generation
- Runtime/network behavior

### IIS Control Plane (This Repo)

Owns integration and orchestration:

- Admin-facing API endpoints for policy and monitoring workflows
- Typed `LinuxDefenseEngineClient` for remote engine communication
- Telemetry cache/refresh orchestration
- Policy push queueing/synchronization
- Background synchronization (`DefenseEngineSyncService`)
- Windows Event Log and IIS deployment adapter hooks

## Control Plane Layers

```text
ASP.NET Core (IIS-hosted)
  ├─ Management APIs (/api/control/*)
  ├─ Control-plane health + downstream health checks
  ├─ AuthN/AuthZ integration point (enterprise extension)
  ├─ Windows integration point (Event Log / PowerShell deployment helper)
  ↓
Services + Interfaces
  ├─ IDefenseEngineClient
  ├─ ITelemetryService
  └─ IPolicyService
  ↓
Linux Defense Engine API (remote appliance)
```

## Current API Integration Contract (Provisional)

Because Linux APIs may evolve, this repo currently uses provisional contracts:

- `GET /health`
- `GET /api/v1/telemetry`
- `POST /api/v1/policies`
- `POST /api/v1/escalations/ack`

These route mappings are configurable under `DefenseEngine:ApiRoutes`.

## Configuration

`DefenseEngine` options include:

- `EngineEndpoint`
- `ApiKey` / `BearerToken`
- `TimeoutSeconds`
- `RetryPolicy`
- `ApiRoutes`
- `Sync`

## Development

> Prerequisite: .NET 8 SDK

```bash
dotnet restore anti-scraping-defense-iis.sln
dotnet build anti-scraping-defense-iis.sln
```

## Security and Ethics

This project is intended for defensive security operations and enterprise traffic protection use cases.
