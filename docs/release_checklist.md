# Release Checklist

Use this checklist before cutting a commercial release candidate.

## Build and Test

- `dotnet build anti-scraping-defense-iis.sln`
- `dotnet test anti-scraping-defense-iis.sln`
- `docker build -t ai-scraping-defense-dotnet .`
- `docker compose up --build` smoke run succeeds
- `docker compose -f compose.yaml -f compose.observability.yaml config` validates the packaged monitoring overlay

## Runtime Validation

- `/health` reports healthy with production-like dependencies
- management dashboard login works
- authenticated operator endpoints work
- webhook intake accepts and processes a known-good sample event
- configured webhook/SMTP/community-report deliveries succeed or fail observably during a sample intake event
- suspicious traffic is tarpitted and can escalate to a block
- Prometheus metrics are reachable when enabled
- OTLP export is verified if configured
- Grafana loads the bundled dashboard against the packaged Prometheus data source

## Security and Configuration

- management API key is non-empty and not a default placeholder
- intake API key is non-empty if `/analyze` is enabled
- Redis does not use a loopback endpoint in production unless explicitly allowed
- proxy trust configuration matches the deployment topology
- production secrets are injected from the deployment platform, not committed config

## Data and Operations

- SQLite audit path is persistent and writable
- Redis persistence/backup expectations are defined
- PostgreSQL schema is initialized if Markov tarpit mode is enabled
- operator runbook has been validated against the target environment

## Release Artifacts

- `README.md` matches the shipped behavior
- `docs/parity_matrix.md` reflects the current status honestly
- `docs/release_artifacts.md` matches the tag and image policy implemented in CI
- `CHANGELOG.md` has an `Unreleased` entry summarizing the release-hardening work
- the tagged release workflow publishes to GHCR and emits both signature and provenance metadata
- image provenance verification succeeds with `gh attestation verify`
- remaining deferred parity items are tracked as GitHub issues, not hidden in review threads
