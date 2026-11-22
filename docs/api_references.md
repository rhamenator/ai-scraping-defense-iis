# API References (.NET)

This document describes the current API surface exposed by the .NET commercial-v1 runtime.

## Endpoint Exposure Rules

- `GET /` and `GET /health` are always exposed.
- Tarpit route exposure is always enabled at `DefenseEngine:Tarpit:PathPrefix` (default `/anti-scrape-tarpit`).
- `/defense/*` management endpoints are only exposed when `DefenseEngine:Management:ApiKey` is set.
- `POST /analyze` is only exposed when `DefenseEngine:Intake:ApiKey` is set.
- `GET /peer-sync/signals` is only exposed when `DefenseEngine:PeerSync:ExportApiKey` is set.

## Public Endpoints

### `GET /`

Returns service metadata and the currently advertised endpoint map.

### `GET /health`

Checks core dependencies (currently Redis connectivity) and returns `healthy` or `degraded`.

### `GET {DefenseEngine:Tarpit:PathPrefix}/{**path}`

Serves deterministic tarpit content and optional tarpit artifacts.

Default route pattern:

- `GET /anti-scrape-tarpit/{**path}`

## Management Endpoints (`/defense/*`)

When enabled, endpoints require the management API key header configured by:

- `DefenseEngine:Management:ApiKeyHeaderName` (default `X-API-Key`)

Dashboard session endpoints:

- `GET /defense/dashboard`
- `GET /defense/dashboard/session`
- `POST /defense/dashboard/session`
- `DELETE /defense/dashboard/session`

Operational data endpoints:

- `GET /defense/events?count=50`
- `GET /defense/metrics`
- `GET /defense/intake-deliveries?count=50`
- `GET /defense/intake-delivery-metrics`
- `GET /defense/community-blocklist/status`
- `GET /defense/peer-sync/status`

Manual blocklist endpoints:

- `GET /defense/blocklist?ip=203.0.113.10`
- `POST /defense/blocklist?ip=203.0.113.10&reason=manual_block`
- `DELETE /defense/blocklist?ip=203.0.113.10`

## Intake Endpoint

### `POST /analyze`

Enabled only when `DefenseEngine:Intake:ApiKey` is configured. Requires the header configured by:

- `DefenseEngine:Intake:ApiKeyHeaderName` (default `X-Webhook-Key`)

Body shape follows the legacy webhook envelope:

- `event_type`
- `reason`
- `timestamp_utc`
- `details`

`details.ip` (or mapped equivalent) must contain a valid IPv4 or IPv6 address. Accepted payloads are durably enqueued before asynchronous processing.

Typical responses:

- `202 Accepted` when queued
- `400 Bad Request` for invalid IP payloads
- `401 Unauthorized` when API key validation fails

## Peer Export Endpoint

### `GET /peer-sync/signals?count=200`

Enabled only when `DefenseEngine:PeerSync:ExportApiKey` is configured. Requires header:

- `DefenseEngine:PeerSync:ExportApiKeyHeaderName` (default `X-Peer-Key`)

Returns recently blocked decisions in peer-shareable envelope format. Only decisions this node made itself are exported: anything imported from a peer (`peer_sync:*`) or a community list (`community_blocklist:*`) is never re-exported, so a signal cannot loop through a mesh of nodes.

Each signal is JSON with snake_case fields: `ip_address`, `summary`, `signals`, `observed_at_utc`, `decided_at_utc`, plus provenance fields `signal_id` (stable per decision), `origin` (this node's `PeerSync:NodeId`), `expires_at_utc` (when the block lapses), `hops` (0 for a locally originated signal) and `signature` (lowercase hex HMAC-SHA256 over every one of those fields, keyed with `PeerSync:SigningKey`). A node with no `NodeId` or `SigningKey` exports unsigned signals.

Importing (`PeerSync:Peers`):

- `PeerSync:RequireSignedSignals` (default `true`): a signal is imported only if it carries a valid signature made with the peer's `VerificationKey` (the exporter's `SigningKey`), its `origin` equals the peer's configured `Name`, and it has an id and an expiry.
- `PeerSync:MaximumHops` (default `0`): signals that travelled more hops than this are rejected. Signals that originated from this node's own `NodeId` are rejected as loops.
- Expired signals are rejected; an accepted block keeps the signal's own expiry, capped at this node's block lifetime. A signal id already imported is ignored.
- Peers default to `ObserveOnly`; a signal only blocks automatically when the peer is explicitly set to `BlockList`.
- Setting `RequireSignedSignals` to `false` accepts unsigned signals as before (a signature that is present is still checked when a key is configured). Legacy feeds that are plain IP arrays are unsigned and need this setting.

## Notes for Integrators

- API behavior is controlled by `DefenseEngine` configuration in `RedisBlocklistMiddlewareApp/appsettings.json`.
- For operational workflows and rotation procedures, see `operator_runbook.md`.

---
