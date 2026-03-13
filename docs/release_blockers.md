# Release Blocker Checklist

This document turns release readiness into a tracked execution queue. Each blocker is intended to move through the same workflow:

1. Problem
2. GitHub issue
3. Code
4. PR
5. Bot review
6. Fix review findings
7. Move to next blocker

## Blocker Queue

| Order | Status | Blocker | Why it blocks release |
| --- | --- | --- | --- |
| 1 | Done | Protect operational endpoints and event data with authentication/authorization | The current stack exposes defense telemetry and recent decisions publicly. |
| 2 | Done | Make proxy/client IP handling production-safe by default | Misconfigured forwarding can block reverse proxies or misattribute attacks. |
| 3 | Done | Replace in-memory event storage with durable audit/event persistence | A security product needs restart-safe auditability and investigation history. |
| 4 | Done | Replace lossy queue behavior with durable or backpressure-aware intake | Dropping suspicious events under load undermines the product during attacks. |
| 5 | Done | Add automated tests for edge filtering, tarpit routing, auth, and persistence | A successful build alone is not release confidence. |
| 6 | In Progress | Add production configuration validation and startup fail-fast checks | Default localhost Redis and empty trusted-proxy config are not market-safe defaults. |
| 7 | Todo | Add operational observability and admin controls | Release needs authenticated admin access, metrics, and actionable diagnostics. |
| 8 | Todo | Close parity gaps required for the first commercial scope | The repo still declares itself a foundation/WIP rather than a releasable product. |

## Blocker 1

### Problem

The application exposes `/defense/events` with no authentication, and the payload includes IP addresses, paths, scores, and defense signals. That is sensitive operational security data and should not be publicly accessible.

### Definition of Done

- `/defense/events` requires authentication.
- The auth mechanism is configurable and documented.
- Unauthorized requests receive `401` or `403`.
- Authenticated requests continue to work.
- Health remains available without exposing sensitive internals.
- The change is validated by automated tests.
- Core request inspection behavior has regression coverage so future release blockers can build on a stable test baseline.

## Blocker 2

### Problem

The application currently relies on forwarded-header trust configuration that is easy to misapply in real proxy/CDN deployments. Without an explicit operating mode and validated trusted proxy list, the service can attribute requests to the wrong IP address and block infrastructure instead of the real client.

### Definition of Done

- Client IP resolution mode is explicit and documented.
- Trusted proxy configuration is validated on startup.
- Forwarded headers are only enabled when the app is explicitly placed into trusted-proxy mode.
- The behavior is covered by automated tests.

## Blocker 3

### Problem

Defense decisions currently disappear when the process restarts because the event feed is backed only by in-memory state. A market-facing security product needs restart-safe audit history so operators can investigate and trust the system's automated actions.

### Definition of Done

- Defense decisions are persisted durably.
- `/defense/events` reads from durable storage.
- Persistence is created automatically and documented.
- The behavior is covered by automated tests.

## Blocker 4

### Problem

The suspicious-request queue currently discards older entries when the queue reaches capacity. That makes the system least reliable when it is under attack pressure, which is exactly when it most needs to preserve evidence.

### Definition of Done

- Suspicious-request intake no longer silently drops older items by default.
- Queue pressure behavior is explicit and documented.
- The behavior is covered by automated tests.

## Blocker 5

### Problem

The project now has a meaningful test baseline, but release confidence still depends on directly covering the routing and bypass decisions that determine whether suspicious traffic is inspected, tarpitted, or allowed through to operational endpoints.

### Definition of Done

- Automated tests cover edge filtering and bypass behavior.
- Automated tests cover tarpit routing behavior.
- Automated tests cover auth and persistence behavior.
- The release checklist can mark the testing blocker done.

## Blocker 6

### Problem

The application still allows clearly development-only defaults like loopback Redis endpoints to boot in production. A market-facing release should fail fast when those unsafe defaults are still present.

### Definition of Done

- Startup fails fast in production when unsafe defaults are still configured.
- The validation is documented.
- The behavior is covered by automated tests.
