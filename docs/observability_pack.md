# Observability Pack

This repository ships a starter observability bundle for the commercial .NET deployment path. It is designed to make the built-in Prometheus metrics and OTLP trace export operational without forcing operators to wire the whole stack from scratch.

## Included Assets

- `compose.observability.yaml` extends the base compose stack with Prometheus, Grafana, and an OpenTelemetry Collector.
- `deploy/observability/prometheus/prometheus.yml` scrapes the app and collector metrics.
- `deploy/observability/prometheus/alert_rules.yml` provides starter rules for block spikes, queue pressure, sync failures, and intake-delivery failures.
- `deploy/observability/grafana/...` provisions a dashboard and data source automatically.
- `deploy/observability/otel-collector-config.yaml` receives OTLP traces from the app and exports them through the collector's debug exporter by default.

## Running the Stack

Start the base runtime plus observability services:

```bash
docker compose -f compose.yaml -f compose.observability.yaml up --build
```

The stack will expose:

- app: `http://localhost:8080`
- Prometheus: `http://localhost:9090`
- Grafana: `http://localhost:3000`
- OTLP gRPC: `localhost:4317`
- OTLP HTTP: `localhost:4318`
- Collector health: `http://localhost:13133`

Grafana defaults:

- username: `admin`
- password: `admin`

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

## Validation

Validate the compose configuration:

```bash
docker compose -f compose.yaml -f compose.observability.yaml config
```

Check that the app metrics are visible:

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
