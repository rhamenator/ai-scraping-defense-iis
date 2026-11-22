# Changelog

<!-- markdownlint-disable MD024 -->

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Fixed

- Multi-hop proxy chains no longer skip inspection (#187): in `TrustedProxy` mode the forwarded chain is walked from the right through every configured trusted hop (new `Networking:TrustedProxyHopLimit`, default 0 = the whole verified chain), and when no client can be determined protected routes now fail closed with 403 instead of passing through. Deployments behind more proxies than they list will see 403 plus an error log until the missing addresses are added to `TrustedProxies` or `TrustedCdnProxies`.
- Hardened the Kubernetes split example (#185, #186): Redis requires a password that every client sends and is reachable only from the defense pods (new `redis-password` secret key and NetworkPolicy); the edge is no longer a LoadBalancer and is published through TLS Ingress objects, with the `/defense` management routes restricted to an allowlist. The management session cookie is now always Secure outside the Development environment, including behind a TLS-terminating proxy. Still open under #186: a startup check that refuses to run management without an explicitly configured secure transport. See `docs/runtime_topologies.md`.
- Installer signing certificates and passwords are no longer imported or used outside tag builds, so pull-request and branch-push jobs cannot reach them (partial fix for #193). A protected GitHub Environment with required reviewers, and a separate build/sign split, still need to be set up in repository settings.
- Installer workflows validate the release tag against the exact SemVer grammar before using it, and pass the version to the build scripts through environment variables instead of interpolating it into shell source (#192). Tags that are not valid semantic versions now fail the workflow.
- The observability overlay no longer ships a default Grafana administrator password (#188): `GRAFANA_ADMIN_PASSWORD` is required (Compose refuses to start without it) and Grafana publishes its port on `127.0.0.1` only. Set a strong value and put authenticated TLS and network controls in front of any remote listener.
- Webhook intake retries are bounded (#195): a failing item is retried with exponential backoff (`Queue:WebhookRetryBaseMilliseconds` 5000, doubling up to `Queue:WebhookRetryMaxMilliseconds` 300000) and moved to a terminal `dead` state after `Queue:WebhookMaxAttempts` (5) attempts; a payload that cannot be parsed is dead-lettered immediately instead of stopping the reader; the wake-up signal is a single coalesced bounded signal instead of an unbounded queue. Existing inbox databases gain the new `attempts` and `not_before_utc` columns on startup. Not changed: a process crash mid-item does not count as an attempt, and dead items stay in the table for inspection (no purge yet).
- Bounded tarpit ZIP generation and fetch concurrency (#191): arbitrary paths now map to at most four prebuilt archives per rotation, with per-client and global fetch limits, bounded file sizes, cancellation during generation, and an HTML fallback while archives are unavailable. Archive reads no longer wait for generation under a request lock.

### Changed

- Express matching robots.txt audit rules as a filtered sequence, addressing CodeQL quality alert #120 without changing rule selection.

## [1.1.4] - 2026-11-21

### Added

- Flagged tarpit clients now have a Redis per-IP hop limit (`DefenseEngine:Tarpit:MaxHops` = 250, `HopWindowSeconds` = 86400). The hit after the limit returns 403 and stores `Tarpit hop limit exceeded (N hits in Ws)` using the existing blocklist key format and `BlockDurationMinutes` TTL. Set `MaxHops` to `0` to disable it. The dedicated public tarpit origin counts split-topology fetches, including ZIP decoys; Redis failures fail open (#219). Archive generation and concurrency work remains tracked in #191.
- Served tarpit hits flag the client with Python's seven-day ordinary flag expiry (`FlaggedIpTtlSeconds` = 604800); repeat offenders above `RepeatOffenderThreshold` = 3 retain their flags. The public tarpit origin validates trusted-proxy configuration and excludes unresolved proxy addresses from hop accounting.

## [1.1.3] - 2026-11-20

### Changed

- The default block duration is now 24 hours (`DefenseEngine:Redis:BlockDurationMinutes` = 1440, was 240), matching the Python edition's `BLOCKLIST_TTL_SECONDS` default and the Rust edition. Blocks still expire on their own, so a mistaken or spoofed-address block cannot last forever. Set the value explicitly to keep the previous 4 hours (#216).

## [1.1.2] - 2026-11-20

### Fixed

- A generic HTTP client (curl, wget, python-requests) no longer reaches the block threshold on its own. Its usual missing `Accept-Language` and `Accept: */*` header signals are no longer scored on top of the generic client signal (30 + 15 + 15 = 60 blocked every such client after one request); a bare generic client now scores 30 plus frequency, and still reaches 60 with a sensitive path or a high request rate (#213).

## [1.1.1] - 2026-11-20

### Fixed

- Operator dashboard: the Recent decisions table now uses the full width of the console (the panels stack), so the signal and explainability columns are no longer cut off at the panel edge, and signal chips wrap inside their column (#210).

## [1.1.0] - 2026-11-20

### Changed (behavior changes, review before upgrading)

- Default `KnownBadUserAgents` no longer contains `curl`, `wget`, `python-requests` or `Scrapy`. These generic clients now live in the new `GenericClientUserAgents` list: a match adds 30 to the score (signal `generic_http_client:<name>`) and no longer blocks on the first request. Scrapers that send a generic client string plus a sensitive path or a high request rate still reach the block threshold of 60. Set `Heuristics:BlockGenericClients` to `true` to restore the strict behavior. Named AI crawlers and offensive tools (sqlmap, nikto, masscan, nmap, zgrab) still block on the first request. Operators who overrode `KnownBadUserAgents` keep their list as written.
- A client that requests a path disallowed by the loaded robots.txt is now blocked (`robots_violation`), whatever its User-Agent claims. The shipped example policy (`Defaults/robots.txt`) disallows `/admin`, `/wp-admin`, `/.env` and similar paths, so a visitor who browses to `/admin` is blocked. Replace it with your origin's robots.txt via `DefenseEngine:Robots:PolicyPath`, or set `DefenseEngine:Robots:Enabled` to `false`.

### Added

- RFC 9309 robots.txt handling: the policy is loaded at startup (a missing file stops startup), served at `/robots.txt`, and enforced at the edge. Anchored rules (`$`) outrank the same text without the anchor.
- A per-address request-rate counter (`Redis:RequestRateKeyPrefix`) and a `high_request_rate` signal at `Heuristics:RequestRateSignalThreshold` requests per `Redis:FrequencyWindowSeconds` (default 120; 0 disables). It is weak evidence: on its own it records one `observed` decision per window and never queues, tarpits or blocks (volume alone must not contain a busy browser or a shared NAT). Combined with other signals it is queued with them for scoring.
- `X-Robots-Tag: noindex, nofollow` on every tarpit response and a robots meta tag in tarpit pages.

### Fixed

- Operator dashboard: the IP and path columns no longer wrap one character per line, and the right-hand status cards no longer overlap at about 1200 px.

## [1.0.6] - 2026-11-13

### Fixed

- Send only supported fields to Request Guard MCP classification and treat MCP
  tool errors as unavailable assessments.

### Changed

- Updated routine NuGet dependencies and GitHub Actions pins after CI and
  installer validation.

## [1.0.5] - 2026-11-02

### Added

- Added distributed-attack operator alerts with Redis cooldown, configured
  delivery channels, and health-gated manual failover guidance.
- Updated the .NET 10 split-runtime and IIS deployment paths, including
  incident-alert configuration and deployment documentation.

### Fixed

- Excluded generated C# build output from CodeQL and selected the .NET 10
  test runner for xUnit 4.

### Changed

- Updated NuGet dependencies and the Docker login action after CI and
  installer validation.

## [1.0.4] - 2026-10-22

### Changed

- Updated NuGet dependencies and pinned GitHub Actions after CI and installer
  workflow verification.

## [1.0.3] - 2026-10-21

### Fixed

- Dispose Raft clusters asynchronously during failed startup and host shutdown,
  and retry the three-node integration fixture only when ephemeral port binding
  loses its unavoidable discovery race.
- Normalize equivalent IP addresses before Redis blocklist reads and writes.
- Pin the patched transitive PKCS dependency in the edge gateway.

## [1.0.2] - 2026-08-12

### Fixed

- Clamp Raft TCP connection timeouts to the lower election timeout so failed peer connections cannot stall leader election.
- Keep the observability Compose overlay and Prometheus target aligned with the split `edge-gateway` service name.
- Validate split-topology service URLs and credentials at startup instead of silently accepting an incomplete deployment contract.
- Preserve the retired Python/IIS implementation under `archive/legacy-python-iis` with provenance and restoration instructions instead of deleting it.
- Exercise SQL Server decision, feedback, delivery, and webhook-inbox persistence with a real container-backed integration test.
- Fix SQL Server webhook claims by selecting the lease columns in the updatable CTE before changing their values.
- Removed a high-severity SSH.NET advisory from integration-test infrastructure by
  upgrading Testcontainers and enforcing the patched transitive dependency floor.
- Hardened logging and Raft disposal paths, corrected retry-delay precision, and
  pinned third-party installer publishing actions to an audited commit.

### Added

* Three-node Raft integration coverage for leader election, log replication, failover, and post-failover replication.
* End-to-end split-topology coverage across the edge gateway, escalation engine, and dedicated tarpit runtime.
* Operator dashboard search, action filtering, and full decision drill-down.
* Apple Silicon installer support with an explicit fail-closed fallback for the
  optional Microsoft.ML adapter, whose native targets do not support `osx-arm64`.
* Windows installer tooling for the .NET runtime, including Windows service support, release staging scripts, and an Inno Setup manifest.
* macOS packaging scripts and CI workflows for unsigned `osx-x64` and `osx-arm64` `.pkg` artifacts, plus operator guidance for SmartScreen and Gatekeeper warnings.
* Windows ARM64 installer packaging and release automation alongside the existing Windows x64 installer path.
* Post-v1 parity baseline documents for optional split runtime topology, escalation scoring/provider expansion, tarpit content strategy, community blocklist + peer coordination, observability/telemetry export, multi-node durability/coordination, and operator UI workflow parity.
* Repository-wide NuGet audit enforcement and weekly NuGet/GitHub Actions dependency updates.

### Changed

* Documentation navigation and parity planning references were updated across `docs/index.md`, `docs/commercial_scope.md`, and `docs/dotnet_parity_roadmap.md` to align with the new baseline documents.
* The unsupported Python services and their IIS/FastCGI examples were moved to `archive/legacy-python-iis` with provenance and restoration guidance instead of being deleted.

## [1.0.0] - 2026-03-15

### Added

* Containerized deployment artifacts for the .NET runtime (`Dockerfile`, `compose.yaml`).
* An observability overlay with Prometheus, Grafana provisioning, alert rules, and an OTLP collector example (`compose.observability.yaml`, `deploy/observability/*`).
* A GitHub Actions workflow that restores, builds, and tests the solution on every push and pull request.
* A tagged release workflow that publishes signed GHCR images with provenance (`.github/workflows/release-images.yml`).
* A containerized integration-test project covering suspicious request handling, management blocklist operations, webhook intake, and PostgreSQL-backed tarpit rendering.
* A .NET-native local trained-model path plus trainer CLI for the escalation engine (`AiScrapingDefense.ModelTrainer`, `DefenseEngine:Escalation:LocalTrainedModel`).
* A .NET-native dataset builder for local model retraining from W3C logs or request-export JSONL plus operator feedback overrides.
* A first-class Slack Incoming Webhook alert channel for intake processing, with persisted delivery outcomes alongside webhook/SMTP/community reporting.
* Rotating ZIP decoy archives with deterministic fake JavaScript payloads in the tarpit path.
* Prometheus/OTLP observability options and runtime instrumentation for defense decisions, tarpit responses, webhook intake, and sync activity.
* Queue depth and capacity telemetry for queued suspicious-request pressure monitoring.
* Release documentation for parity, operator operations, and release validation.

### Changed

* Suspicious requests now render tarpit responses directly from middleware instead of relying on late path rewrites.
* The fallback 404 path now uses `MapFallback` so routed endpoints remain reachable in real hosts and test hosts.
* Operator and intake endpoints are explicitly bypassed by edge inspection to avoid tarpitting internal control surfaces.

## [0.0.3] - 2025-04-22

### Added

* **PostgreSQL Markov Backend:** Tarpit content generation now uses Markov chains stored in PostgreSQL for persistence and scalability.
* **PostgreSQL Training Script:** New script (`rag/train_markov_postgres.py`) to populate the Markov database from a text corpus.
* **Tarpit Hop Limit:** Configurable limit (`TAR_PIT_MAX_HOPS`, `TAR_PIT_HOP_WINDOW_SECONDS`) on tarpit requests per IP, tracked in Redis (`REDIS_DB_TAR_PIT_HOPS`). Exceeding limit triggers IP block.
* **Deterministic Tarpit Generation:** Tarpit pages and links are now generated deterministically based on URL hash and system seed (`SYSTEM_SEED`), creating a stable fake site structure.
* **Example Kubernetes PostgreSQL Manifest:** Added `kubernetes/postgres-statefulset.yaml` as an example deployment.
* **Example Main Website Nginx Config:** Added `example_main_website_nginx.conf` to illustrate proxying to the anti_scrape stack.
* **Configurable JSON Metrics Logging:** Added option (`LOG_METRICS_TO_JSON`) to periodically dump metrics to a JSON file for debugging.
* **PostgreSQL Dependency:** Added `psycopg2-binary` to `requirements.txt`.

### Changed

* **Nginx `robots.txt` Handling:** Updated `nginx/lua/detect_bot.lua` to check `robots.txt` rules (simplified list) for known benign bots before allowing or tarpitting.
* **Nginx Configuration for Separate Deployment:** Modified `nginx/nginx.conf` to proxy allowed traffic to a backend defined by `REAL_BACKEND_HOST` environment variable.
* **Tarpit Content Generation:** Replaced `markovify` logic with PostgreSQL queries in `tarpit/markov_generator.py`.
* **Configuration Files:** Updated `docker-compose.yml` and `kubernetes/configmap.yaml` with new environment variables for PostgreSQL, Tarpit hop limit, and corrected names.
* **Tarpit API (`tarpit_api.py`):** Integrated hop limit check logic, Redis connections for hops and blocklist, and deterministic seeding.

### Fixed

* **Environment Variable Naming:** Corrected environment variable names containing "TAR PIT" to use underscores (e.g., `TAR_PIT_MAX_HOPS`).

### Added in 0.0.2 - 2025-04-20

* Kubernetes manifests (`/kubernetes`) for deploying services, including ConfigMaps, Secrets structure, Deployments, StatefulSet (Redis), and Services.
* Initial structure for NGINX reverse proxy with Lua support.
* FastAPI service skeleton for tarpit (`/tarpit`).

### Added in 0.0.1 - 2025-04-19

* Initial structure for NGINX reverse proxy with Lua support.
* FastAPI service skeleton for tarpit (`/tarpit`).
* FastAPI service skeleton for escalation engine (`/escalate`).
* Flask service skeleton for Admin UI (`/admin`, `/metrics`).
* Basic `metrics.py` module for in-memory stats tracking.
* Python script placeholders for Markov generator, JS ZIP generator, email entropy scanner, webhook forwarder.
* Docker Compose setup for multi-container orchestration.
* Base `Dockerfile` with core dependencies.
* `README.md`, `LICENSE` (GPL-3.0), `LICENSE.md`, `CONTRIBUTING.md`, `SECURITY.md`.
* `.gitignore` file.
* GitHub Actions CI workflow (`ci.yml`) and PR labeler (`labeler.yml`).
* Issue and Pull Request templates (`.github/`).
* Documentation structure (`/docs`) with placeholders for architecture, API reference, legal info.
* Synthetic corpus generator (`synthetic_data_generator.py`).
* Wikipedia random page scraper (`wiki_scraper_generator.py`).
* Slow streaming HTML response logic in tarpit API.
* Rotating archive logic for JS honeypots.
* Webhook integration in escalation engine.
* Fake page generation with Markov text and randomized links.
* Admin UI template (`admin_ui/templates/index.html`).
* GoAccess configuration (`goaccess/goaccess.conf`).
* Redis-based IP blocklisting checked by Nginx/Lua (`check_blocklist.lua`).
* AI Service (`ai_webhook.py`) to receive webhooks, manage blocklist, and send alerts (Slack, SMTP, Webhook).
* Redis-based frequency tracking in Escalation Engine.
* Random Forest model training script (`rag/training.py`) using log data, feedback, and feature extraction.
* Support for Docker secrets for sensitive configuration.
* Enhanced logging using Python's `logging` module in backend services.
* Basic health check endpoints for backend services.
* Resource limits and basic healthcheck in `docker-compose.yml`.
* Recommended security headers in `nginx.conf`.
* Detailed "Production Considerations" section in `docs/getting_started.md`.
* `sample.env` file.

### Changed

* Refined escalation pipeline logic (heuristic -> RF model -> local LLM -> external API -> webhook).
* Improved honeypot field obfuscation using dynamic generation and realistic naming.
* Made tarpit response streaming more robust and randomized delay.
* Switched Markov generator corpus source from Wikipedia scraping to synthetic data, then back to optional Wikipedia scraping based on user preference.
* Refined directory structure for clarity (e.g., separating services).
* Updated `docker-compose.yml` to include all services, networks, volumes, secrets, and resource limits.
* Enhanced Nginx Lua bot detection (`detect_bot.lua`) with more header checks and scoring logic.
* Improved error handling and logging in backend Python services.
* Updated `nginx.conf` with security headers, refined rate limits, and HTTPS placeholders.
* Renamed `nginx.conf.txt` to `nginx.conf`.

### Fixed

* Corrected placeholder count mismatch in JS template generator.
* Fixed file path issues for Windows compatibility during testing.
* Removed emojis from README for better PDF compatibility.
* Ensured consistent timestamp handling (UTC ISO format) across services.
* Improved robustness of Redis connections and operations.

## [0.0.0] - 2025-04-18

### Added

* Initial stable release of the AI Scraping Defense Stack.
* Core functionality: NGINX/Lua detection, Tarpit API, Basic Escalation, Admin Metrics UI.
* Documentation V1.
