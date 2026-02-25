# RedisBlocklistMiddlewareApp (Refactored as Control Plane Adapter)

The ASP.NET Core app is now the **IIS control plane adapter** for the Linux defense engine.

## What it does

- Exposes control-plane endpoints under `/api/control/*`
- Uses a typed Linux engine client (`LinuxDefenseEngineClient`)
- Pulls telemetry and caches it for management views
- Pushes policy updates to the Linux engine
- Runs background sync via `DefenseEngineSyncService`

## What it no longer does

- No in-process Redis-based request blocking
- No C# heuristic bot detection implementation
- No tarpit path rewriting logic in middleware

Those execution concerns belong to the Linux defense engine.
