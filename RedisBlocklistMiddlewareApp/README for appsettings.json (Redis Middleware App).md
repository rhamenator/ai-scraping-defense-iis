# README for appsettings.json (Control Plane Adapter)

This file documents configuration for the ASP.NET Core control-plane adapter.

## Sections

### Logging
Standard ASP.NET Core logging levels.

### AllowedHosts
Standard host filtering control.

### DefenseEngine
Remote Linux engine integration settings.

- `EngineEndpoint`: Base URL for Linux defense engine API.
- `ApiKey`: Optional API key sent as `X-API-Key`.
- `BearerToken`: Optional bearer token for Authorization header.
- `TimeoutSeconds`: HTTP timeout for engine calls.
- `RetryPolicy.MaxAttempts`: Maximum retries per request.
- `RetryPolicy.BaseDelayMilliseconds`: Base backoff delay.
- `ApiRoutes.*`: Configurable downstream endpoint paths (`HealthPath`, `TelemetryPath`, `PoliciesPath`, `EscalationAckPath`).
- `Sync.SyncIntervalSeconds`: Background sync interval for telemetry and queued policy updates.

## Validation

`DefenseEngineOptionsValidator` performs startup validation of endpoint and retry/timeout values.

## Notes

- Keep secrets out of source control; set `ApiKey` / `BearerToken` via environment-specific configuration.
- This app does not perform Redis blocklist checks or in-process heuristics.
- Defense behavior remains in the Linux engine.
