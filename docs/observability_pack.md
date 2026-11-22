# Observability Pack

This repository ships a starter observability bundle for the commercial .NET deployment path. It is designed to make the built-in Prometheus metrics and OTLP trace export operational without forcing operators to wire the whole stack from scratch.

## Included Assets

- `compose.observability.yaml` extends the base compose stack with Prometheus, Grafana, and an OpenTelemetry Collector.
- `deploy/observability/prometheus/prometheus.yml` scrapes the edge gateway and collector metrics.
- `deploy/observability/prometheus/alert_rules.yml` provides starter rules for block spikes, queue pressure, sync failures, and intake-delivery failures.
- `deploy/observability/grafana/...` provisions a dashboard and data source automatically.
- `deploy/observability/otel-collector-config.yaml` receives OTLP traces from the edge gateway and exports them through the collector's debug exporter by default.

## Running the Stack

Start the base runtime plus observability services:

```bash
cp compose.env.example .env
# Replace every placeholder in .env with an independent random value.
docker compose -f compose.yaml -f compose.observability.yaml up --build
```

Bound to this host's loopback only (not reachable from other machines):

- Grafana: `http://localhost:3000`
- OTLP gRPC: `127.0.0.1:4317`
- OTLP HTTP: `127.0.0.1:4318`
- Collector health: `http://127.0.0.1:13133`

Published on every network interface by the Compose files, so reachable from other machines unless you restrict them:

- edge gateway: `http://localhost:18080` (published by `compose.yaml`; it is the public-facing service)
- Prometheus: `http://localhost:9090` (**no authentication**: restrict it with your firewall or bind it to loopback yourself)

Grafana signs in with `GRAFANA_ADMIN_USER` (default `admin`) and the `GRAFANA_ADMIN_PASSWORD` you set in `.env`; the stack
will not start without one.

The collector's OTLP receivers have **no authentication**, so Compose publishes them to loopback only and the edge gateway
reaches them over the Compose network. Do not change those bindings to expose them to other machines.

## What the Dashboard Covers

The bundled Grafana dashboard focuses on the core operational questions for this stack:

- suspicious request rate by reason
- block rate over the last 15 minutes
- queue pressure using `ai_scraping_defense_queue_depth` and `ai_scraping_defense_queue_capacity`
- intake delivery failure rate by channel
- tarpit and webhook intake activity
- recent rejected records from community and peer sync

## Alert Rules

Starter Prometheus alerts are bundled for:

- sustained spikes in block decisions
- suspicious-request queue saturation
- rejected community or peer sync imports
- failed webhook, SMTP, or community-report deliveries from the intake pipeline

Treat the thresholds as starting values. They are intentionally conservative and should be tuned against real traffic after deployment.

## OTLP Collector Notes

The collector config is intentionally minimal:

- it receives OTLP traces over gRPC and HTTP
- it batches traces
- it exports them through the `debug` exporter

For a real production trace backend, replace the `debug` exporter in `deploy/observability/otel-collector-config.yaml` with your chosen OTLP, Tempo, vendor, or other supported exporter.

### Limits

The default collector config bounds what an unauthenticated sender can do: gRPC messages and HTTP request bodies are capped at
4 MiB, concurrent gRPC streams at 32, and a `memory_limiter` (first in the pipeline, 256 MiB with a 64 MiB spike allowance)
refuses data rather than growing without bound. The `debug` exporter prints one summary line per batch (counts, not contents);
replace it with a real backend outside development.

### Producers on other machines

Use `deploy/observability/otel-collector-remote.example.yaml` instead of the default config. It requires TLS and a bearer token
(`OTEL_INGEST_TOKEN`, sent as `Authorization: Bearer <token>`), keeps the same limits, and binds the health and telemetry
endpoints to the container's loopback. Mount your `server.crt` and `server.key` at `/etc/otel/tls/`, publish 4317/4318 only on
the interface your producers reach, and put your own firewall in front. Note that Prometheus then cannot scrape the collector's
own metrics at `otel-collector:8888`; point it at a separate collector or change that address deliberately.

## Validation

Validate the compose configuration:

```bash
docker compose --env-file .env -f compose.yaml -f compose.observability.yaml config
```

Check that the edge gateway metrics are visible:

```bash
curl http://localhost:8080/metrics
```

Trigger a suspicious request, then confirm metrics such as:

- `ai_scraping_defense_suspicious_requests_total`
- `ai_scraping_defense_block_decisions_total`
- `ai_scraping_defense_queue_depth`
- `ai_scraping_defense_intake_delivery_total`

Check that the collector is healthy:

```bash
curl http://localhost:13133/
```
