# Runtime topologies

The .NET implementation supports both a single process and three independently scalable runtimes.

## Single runtime (default)

`AiScrapingDefense.EdgeGateway.dll` hosts edge inspection, escalation, and tarpit rendering in one process. Redis remains required for hot state. SQLite is the default event store; PostgreSQL and SQL Server are available for shared durable audit data.

```text
DefenseEngine__Topology__Mode=Single
```

## Split runtimes

Split mode runs these Dockerfile targets independently:

- `edge`: public request inspection, blocklist decisions, audit, and control APIs
- `escalation`: authenticated `POST /v1/assess` scoring/model service
- `tarpit`: public, unprivileged `GET /tarpit/{path}` decoy renderer

Required edge configuration:

```text
DefenseEngine__Topology__Mode=Split
DefenseEngine__Topology__EscalationBaseUrl=http://escalation-engine:8080
DefenseEngine__Topology__TarpitPublicBaseUrl=https://tarpit.example.com
DefenseEngine__Topology__ServiceToken=<random value of at least 32 characters>
```

The escalation runtime must receive the same service token. Edge-to-escalation calls use a bearer token, a bounded timeout, and three attempts. A failed assessment is logged and persisted as an `observed` decision with `analysis_runtime_failure`; it is not silently discarded.

The tarpit is intentionally public: suspicious clients receive a method-preserving `307` redirect and fetch the decoy directly. Ordinary traffic remains on the protected application origin, so it does not incur tarpit-origin or Cloudflare egress. The tarpit exposes no management or mutation API.

Both modes expose `/live` for process liveness and `/health` for dependency/readiness checks. Do not use `/health` as a Kubernetes liveness probe: a dependency or quorum outage should remove a pod from service, not restart a healthy process.

## Client identity and Cloudflare

Use `TrustedProxy` mode only with explicit addresses. Put ordinary proxies and Envoy collectors in `Networking:TrustedProxies`; put Cloudflare's published ranges in `Networking:TrustedCdnProxies`. Cloudflare identity and fingerprint headers are accepted only from the CDN list when `DefenseEngine:Cloudflare:Enabled` is true. Collector headers are accepted only from the non-CDN list, with no fallback across trust boundaries. Missing, malformed, or spoofed origin headers produce `unknown`; the proxy or Cloudflare edge address is never substituted as the block target.

Fingerprint provenance crossing the split-runtime or MCP boundary must also use
the HMAC contract in [Trusted TLS fingerprints](tls_fingerprint_attestation.md).

Cloudflare integration only produces an operator recommendation to enable Under Attack Mode when the integration is enabled and the attack thresholds are met. It never automatically changes the zone mode. Normal outbound traffic is not proxied through Cloudflare by these runtimes.

## Validation

1. `/live` returns 200 for every runtime.
2. `/health` returns 200 after Redis and the Raft leader (if enabled) are ready.
3. Escalation rejects a missing or invalid service token.
4. A suspicious request reaches remote assessment and redirects to the public tarpit.
5. An unavailable escalation runtime creates a visible degraded audit decision.
6. The public edge never treats a configured proxy/CDN address as the originating client.

`compose.yaml` is the local split-runtime example. `deploy/kubernetes/split-consensus.yaml` is the three-edge-node example.
