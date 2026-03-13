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
| 2 | In Progress | Make proxy/client IP handling production-safe by default | Misconfigured forwarding can block reverse proxies or misattribute attacks. |
| 3 | Todo | Replace in-memory event storage with durable audit/event persistence | A security product needs restart-safe auditability and investigation history. |
| 4 | Todo | Replace lossy queue behavior with durable or backpressure-aware intake | Dropping suspicious events under load undermines the product during attacks. |
| 5 | Todo | Add automated tests for edge filtering, tarpit routing, auth, and persistence | A successful build alone is not release confidence. |
| 6 | Todo | Add production configuration validation and startup fail-fast checks | Default localhost Redis and empty trusted-proxy config are not market-safe defaults. |
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
