# Operator Runbook

This runbook covers the first commercial .NET deployment shape: one ASP.NET Core service with Redis for hot state, SQLite for durable audit/intake storage, and optional PostgreSQL for the tarpit Markov corpus.

## Prerequisites

- Redis reachable from the application
- Write access to the configured SQLite audit path
- Optional PostgreSQL instance if `DefenseEngine:Tarpit:PostgresMarkov:Enabled=true`
- Configured management API key for operator access
- Configured intake API key if `/analyze` is enabled

## Local or Single-Node Bring-Up

Use the bundled Compose stack for a quick smoke environment:

```bash
docker compose up --build
```

The app will be available on `http://localhost:8080`.

To include the bundled monitoring stack:

```bash
docker compose -f compose.yaml -f compose.observability.yaml up --build
```

## Required Production Configuration

At minimum, set:

- `DefenseEngine:Redis:ConnectionString`
- `DefenseEngine:Management:ApiKey`
- `DefenseEngine:Intake:ApiKey` if webhook intake is required
- `DefenseEngine:Intake:Alerting:*` if webhook or SMTP alerts are required
- `DefenseEngine:Intake:CommunityReporting:*` if outbound community reporting is required
- `DefenseEngine:Audit:DatabasePath`
- `DefenseEngine:Networking:ClientIpResolutionMode`

When behind a reverse proxy or CDN:

- set `DefenseEngine:Networking:ClientIpResolutionMode=TrustedProxy`
- populate `DefenseEngine:Networking:TrustedProxies`

## Health and Smoke Checks

Check health:

```bash
curl http://localhost:8080/health
```

Check the root descriptor:

```bash
curl http://localhost:8080/
```

Check authenticated metrics:

```bash
curl -H 'X-API-Key: <management-key>' http://localhost:8080/defense/metrics
```

Check intake delivery metrics:

```bash
curl -H 'X-API-Key: <management-key>' http://localhost:8080/defense/intake-delivery-metrics
```

## Dashboard Access

Open:

```text
http://localhost:8080/defense/dashboard
```

Authenticate with the configured management API key. The dashboard can create a signed session cookie so the browser can call the operator endpoints without repeating the header manually.

## Manual Blocklist Operations

Inspect:

```bash
curl -H 'X-API-Key: <management-key>' \
  'http://localhost:8080/defense/blocklist?ip=198.51.100.10'
```

Block:

```bash
curl -X POST \
  -H 'X-API-Key: <management-key>' \
  'http://localhost:8080/defense/blocklist?ip=198.51.100.10&reason=manual_block'
```

Unblock:

```bash
curl -X DELETE \
  -H 'X-API-Key: <management-key>' \
  'http://localhost:8080/defense/blocklist?ip=198.51.100.10'
```

## Webhook Intake

Submit a webhook event:

```bash
curl -X POST \
  -H 'Content-Type: application/json' \
  -H 'X-Webhook-Key: <intake-key>' \
  http://localhost:8080/analyze \
  -d '{
    "event_type": "ml_verdict",
    "reason": "confirmed_bot",
    "timestamp_utc": "2026-03-15T00:00:00Z",
    "details": {
      "ip": "198.51.100.77",
      "method": "GET",
      "path": "/pricing",
      "query_string": "",
      "user_agent": "example-bot",
      "signals": ["model_positive"]
    }
  }'
```

Inspect recent intake delivery attempts:

```bash
curl -H 'X-API-Key: <management-key>' \
  'http://localhost:8080/defense/intake-deliveries?count=20'
```

## Community and Peer Sync Checks

Community blocklist status:

```bash
curl -H 'X-API-Key: <management-key>' \
  http://localhost:8080/defense/community-blocklist/status
```

Peer-sync status:

```bash
curl -H 'X-API-Key: <management-key>' \
  http://localhost:8080/defense/peer-sync/status
```

Peer export:

```bash
curl -H 'X-Peer-Key: <peer-export-key>' \
  'http://localhost:8080/peer-sync/signals?count=50'
```

## Metrics and Tracing

If `DefenseEngine:Observability:EnablePrometheusEndpoint=true`, scrape:

```bash
curl http://localhost:8080/metrics
```

If `DefenseEngine:Observability:OtlpEndpoint` is configured, traces are exported to that collector.

The bundled observability overlay provisions:

- Prometheus at `http://localhost:9090`
- Grafana at `http://localhost:3000`
- an OpenTelemetry Collector with OTLP on `4317` and `4318`

See [observability_pack.md](observability_pack.md) for the packaged monitoring assets and default alert rules.

## Backup and Recovery

- Back up the SQLite audit database at `DefenseEngine:Audit:DatabasePath`
- Back up Redis if you rely on durable Redis persistence for operational recovery
- Back up PostgreSQL if you maintain a curated Markov corpus

## Common Failure Cases

- `503 /health`: Redis is unreachable or misconfigured
- startup failure in `Production`: loopback Redis or unsafe proxy config is still present
- missing real client IPs: proxy/CDN trust mode is not configured correctly
- intake delivery failures: inspect `/defense/intake-deliveries` for failed webhook, SMTP, or community-report attempts
- empty Markov tarpit output changes: PostgreSQL corpus is empty or unavailable, so synthetic fallback content is being used
