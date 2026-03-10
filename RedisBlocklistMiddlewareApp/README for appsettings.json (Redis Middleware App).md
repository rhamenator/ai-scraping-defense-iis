# README for appsettings.json (Control Plane Adapter)

This file documents configuration for the ASP.NET Core **control plane adapter**.

## Sections

### Logging
Standard ASP.NET Core logging levels.

### AllowedHosts
Standard host filtering control.

### DefenseEngine
Remote Linux engine connection settings.

- `EngineEndpoint`: Base URL for Linux defense engine API.
- `ApiKey`: Optional API key sent as `X-API-Key`.
- `BearerToken`: Optional bearer token for Authorization header.
- `TimeoutSeconds`: HTTP timeout for engine calls.
- `RetryPolicy.MaxAttempts`: Max retries per request.
- `RetryPolicy.BaseDelayMilliseconds`: Base backoff delay.

## Notes

- Keep secrets out of source control; set `ApiKey` / `BearerToken` via environment-specific configuration.
- This app no longer performs Redis blocklist checks or in-process heuristics.
- Defense behavior remains in the Linux engine.


### Additional integration settings

- `DefenseEngine:ApiRoutes:*` controls downstream Linux endpoint paths used by the adapter client.
- `DefenseEngine:Sync:SyncIntervalSeconds` controls periodic telemetry/policy sync cadence.
