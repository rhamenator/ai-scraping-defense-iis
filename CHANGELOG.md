# Changelog

<!-- markdownlint-disable MD024 -->

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Changed

- **Upgrade in two steps:** DotNext is now 6.12.2 (#196, supersedes the Dependabot PR). Run the new `tools/RaftLogExporter` against the stopped node's storage directory first; the Raft log is then imported from its export, and a node with an old log and no export refuses to start and says how to proceed. Consensus membership is now static (`Consensus:Members`, recorded in `members.json`). See `docs/migrations.md`.

### Added

- Raft snapshots and log compaction (#196): a blocklist snapshot every `Consensus:SnapshotEveryCommands` (default 1000) commands; lagging followers are brought up with a snapshot. `docs/migrations.md` documents the reusable migration pattern.

### Changed

- **Breaking default:** the management API and dashboard now require HTTPS (#186). New setting `DefenseEngine:Management:RequireSecureTransport`, on by default. In Production the app refuses to start when management is enabled and no HTTPS is provided (an `https://` listener, a TLS-terminating proxy in `TrustedProxy` mode with `Networking:TrustedProxies` set, or IIS, which owns the bindings), and every `/defense` request that did not arrive over HTTPS (after `X-Forwarded-Proto` from a trusted proxy is applied) gets 403. The Development environment is exempt, and HTTPS configured through `ASPNETCORE_URLS`, `Kestrel:Endpoints`, or `ASPNETCORE_HTTPS_PORTS` all count. **Upgrading an HTTP-only install:** set `DefenseEngine__Management__RequireSecureTransport=false` to keep the old behaviour; Production logs a warning at each start while it is off. The shipped `compose.yaml` (single-host HTTP quick start, via `ASD_REQUIRE_SECURE_MANAGEMENT`) and the Kubernetes manifest (TLS ends at the Ingress and the trusted-proxy range is cluster specific) opt out explicitly, with comments on how to turn it on. `scripts/check_secure_transport_locally.sh` verifies the behaviour end to end locally.

### Security

- The audit database size is now monitored and bounded (part of #197). New setting `Audit:MaxDatabaseBytes` (0 = off). The size (SQLite live bytes `(page_count - freelist_count) * page_size`, PostgreSQL database, SQL Server data files) is published as `ai_scraping_defense_audit_storage_bytes` next to `ai_scraping_defense_audit_storage_limit_bytes`, the new `AiScrapingDefenseAuditStorageNearLimit` Prometheus alert fires above 80 percent, from 90 percent the stores stop saving the score breakdown (the bulkiest column) so decisions keep being recorded (resuming when the database shrinks), and at 100 percent the monitor prunes the oldest ~10 percent of decision rows (floor 1,000, with a 5-minute cooldown, keeping feedback-referenced rows).
- Prometheus in the bundled observability stack no longer exposes an unauthenticated stop and reload API (#190). It was started with `--web.enable-lifecycle` and published on every host interface, so anyone who could reach port 9090 could `POST /-/quit` to stop it (confirmed against the pinned image: the process exited) or `/-/reload` to reload its configuration. The flag is removed (the endpoints now answer 403), port 9090 is published to `127.0.0.1` only, and the service gets `restart: unless-stopped`. CI checks the loopback binding and that no lifecycle or admin-API flag is set. **Behaviour change:** reload Prometheus's configuration by restarting the service (`docker compose ... restart prometheus`); machines other than this host can no longer reach it.
- Intake requests are now rate limited per credential, and wrong keys cannot be guessed at speed (part of #197). `/analyze` accepted an unlimited number of authenticated requests and an unlimited number of wrong-key attempts. The intake key may now send `Intake:RateLimitPerMinute` requests per minute in total (default 600, however many addresses it uses), and a client address that makes `Intake:FailedAuthLimitPerMinute` wrong-key attempts in a minute (default 30) is refused with 429 and `Retry-After` until the minute ends, a valid key from that address included. 0 disables either limit. State is in memory per instance and bounded. **Behaviour change:** a producer sending more than 600 events a minute now gets 429 and must back off or have the limit raised. Still open on #197: PostgreSQL and SQL Server audit store retention, feedback and delivery table retention, and storage-utilisation monitoring.

- OTLP ingestion in the bundled observability stack is no longer open to the network or unbounded (#189). The collector's OTLP receivers (4317, 4318) and health port (13133) were published on every host interface with no authentication, no request-size cap and no memory limit. Compose now publishes them to `127.0.0.1` only (the edge gateway reaches the collector over the Compose network), and the default collector config caps gRPC messages and HTTP bodies at 4 MiB, limits concurrent gRPC streams, and puts a `memory_limiter` first in the pipeline. `deploy/observability/otel-collector-remote.example.yaml` is an opt-in config for remote producers with TLS and a bearer token. CI now checks that every published collector port is loopback-bound and that both configs pass the collector's own `validate`. **Behaviour change:** OTLP producers on other machines can no longer reach the default stack; use the remote example. The `debug` exporter prints counts only (`verbosity: basic`), so it was left in place for development. `docs/observability_pack.md` also no longer claims a default Grafana password of `admin`: the stack requires `GRAFANA_ADMIN_PASSWORD`.
- Webhook intake and the SQLite audit store are now bounded (part of #197). `/analyze` accepted any event up to the 1 MiB request limit, and the audit store grew without limit (`MaxRecentEvents` only limited reads). Webhook events now have per-field limits (event type 128, reason 1024, method 16, path 2048, query string 4096, user agent 1024, 64 signals of at most 256 characters) and an aggregate `Intake:MaxEventBytes` limit (32 KiB); an event over a limit is refused with 413 and is not queued. The SQLite audit store keeps at most `Audit:MaxStoredEvents` decision rows (default 1,000,000, 0 disables; the oldest are removed at start-up and every 500 inserts, except decisions an operator has given feedback on; dashboard totals are cumulative and survive pruning and restarts) and can age rows out with `Audit:RetentionDays` (off by default: that is a policy decision for the operator). **Behaviour change:** webhook producers that send oversized events now get 413, and a database with more than 1,000,000 stored decisions is trimmed to that many on first start. This does **not** close #197: the PostgreSQL and SQL Server audit stores, the feedback and delivery tables, per-credential rate limits and storage-utilisation alerts are not done yet.
- Model output is now advisory and the OpenAI-compatible adapter is hardened (#201). A model's "human" or "benign crawler" verdict lowered the score directly and ended the adapter chain, the reply was accepted if it merely contained a classification word anywhere (so `NOT_A_MALICIOUS_BOT` counted as `MALICIOUS_BOT`, and an echoed request field could act as a verdict), and every request field went into the prompt untruncated with nothing marking it as untrusted. Now: negative model adjustments are recorded but not applied unless `Escalation:AllowNegativeModelScoreAdjustments` is set (default off; positive verdicts always count); the prompt labels the request data as untrusted, fences it between markers (the data is JSON-encoded so it cannot close the fence), and truncates each field and the signal list; only a JSON object with an exact `classification` value counts, anything else is `INCONCLUSIVE`, and the model's summary is bounded and stripped of control characters; `OpenAiCompatibleModel:UseJsonSchemaResponseFormat` optionally requests schema-constrained output. A JSON array or string reply no longer loses the whole assessment. **Behaviour change:** model "human" and "benign crawler" verdicts no longer lower scores by default (set `AllowNegativeModelScoreAdjustments=true` to keep the old behaviour), and free-text replies that are not JSON are now inconclusive. The other adapters (MCP, local trained) are covered by the score policy but not by the prompt and parsing changes.

- Reputation score adjustments are now validated and score arithmetic is overflow-safe (#202). The HTTP reputation provider accepted any 32-bit `score_adjustment` from its response, and the assessment summed scores in `int` without overflow protection: two adjustments of `2147483647` wrapped to a small negative total that was then floored to zero, so a malicious verdict could disappear, and an adjustment of `-2147483648` crashed the assessment (`Math.Abs` overflow in the summary). Now the provider caps a positive adjustment at `MaxScoreAdjustment` (default 50) and ignores negative ones unless `AllowNegativeScoreAdjustments` is set (then limited to `-MaxScoreAdjustment`); every addition in the assessment saturates at ±1,000,000, and the summary and the configured-range provider no longer overflow. **Behaviour change:** a negative `score_adjustment` from the HTTP provider no longer lowers scores by default; set `AllowNegativeScoreAdjustments=true` if you rely on it. Model adapters and configured ranges use operator-set values and are unchanged apart from the saturating arithmetic.
- The Raft state machine no longer remembers every command identifier it has ever applied (part of #196). Each consensus mutation carries a fresh identifier and the applied-command set only grew, one entry per mutation for the life of the process. It now keeps a bounded window of the most recent 65,536 identifiers (oldest forgotten first); applying a block or unblock again is idempotent, so forgetting an old identifier cannot change the result. This does **not** close #196: the log is still never compacted and there are no snapshots, so startup replay and disk use still grow with the number of mutations, and no mutation-rate limits or log-size quotas exist yet.

- Outbound HTTP responses are now size-bounded before they are parsed (#200). The community blocklist feed, peer signal feed, OpenAI-compatible model adapter and HTTP reputation provider materialised and parsed whatever body the remote server sent, so a hostile or broken endpoint could exhaust memory (the peer record cap applied only after parsing). They now send with `ResponseHeadersRead` and read through a shared bounded reader that rejects an oversized declared length before reading a byte and counts the bytes actually decoded, which also stops a body without a length and a compressed body that expands. Limits: 5 MiB community feed, 2 MiB peer feed, 4 MiB model response, 256 KiB reputation answer. An oversized community or peer response fails that fetch with `ResponseTooLargeException`; the model and reputation providers log it and return no assessment, as for any other failure.

### Security

- The Docker build can no longer copy credentials into an image layer (#194). `scripts/configure_credentials.py` writes real credentials to `.env` in the repository root, `.dockerignore` did not exclude it, and the build stage ran `COPY . .`, so the secrets landed in the build stage's layers and any shared build cache. `.dockerignore` now excludes `.env`, `.env.*` and common key and certificate files, and the Dockerfile copies only `Directory.Build.props`, `global.json` and the four project folders the published apps need, restoring from the project files first. A test fails if a broad copy returns or a needed project is missing. **If images were built with a shared or registry-backed build cache after running the credential script, treat those credentials as exposed: rotate them and invalidate the cache.**
- Webhook credentials no longer reach delivery records or logs (#199). The delivery audit stored the full Slack, generic-webhook and community-report URLs (which carry their secret in the path or query) and raw exception text, and the management API returned them. Records now hold only the origin plus a short fingerprint, URLs inside error text are redacted, both the SQLite and relational stores redact on write and read, and rows written by earlier releases are rewritten when the store opens. The HTTP client factory's own logging, which printed each request's URL path at Information level, is removed. **Rotate any webhook URL an earlier release may have stored or logged.**

### Added

- Operator dashboard improvements (#235, #236, #237, #238): block and unblock now ask for confirmation naming the normalized address and its current status, and a reason is required to block (the silent `manual_block` default is gone); the decision detail has a "Was this decision wrong?" form that records feedback through `/defense/feedback` and lists the feedback already recorded for that decision; a Consensus card (role, term, leader, and a no-leader warning) appears when consensus is enabled; and the filtered decision table can be exported as CSV or JSON, with spreadsheet formulas neutralised in text cells.
- Consensus (when enabled) now stores its Raft log with DotNext's `WriteAheadLog` in `<StoragePath>/wal` instead of the `DiskBasedStateMachine` directory `<StoragePath>/log`, with a one-time automatic migration on first start (committed entries, terms and exact bytes are copied; the old directory is kept as `log.legacy-<timestamp>`; a failed migration changes nothing and the node does not start). This prepares the later move to DotNext 6. Upgrade all nodes of a cluster together; see `docs/consensus_log_storage.md` for the details, including that uncommitted trailing entries are not migrated and that committed entries are no longer replayed into Redis on each restart.

### Fixed

- The Kubernetes split example can now form its Raft cluster: the edge StatefulSet uses `podManagementPolicy: Parallel`. Its readiness probe waits for a Raft leader, which needs a quorum, so the default ordered start left pod 0 never Ready and pods 1 and 2 never created (found by running the example in a lab cluster). `podManagementPolicy` cannot be changed on an existing StatefulSet: delete it with `--cascade=orphan` and re-apply.
- The relational (Postgres and SQL Server) webhook inbox no longer crashes the host when it is empty (found by running the Kubernetes example in a lab cluster): claiming from an empty inbox committed its transaction while the data reader was still open, which Npgsql rejects ("A command is already in progress"), so with the Postgres audit provider the edge failed at startup on every fresh start. The reader is now closed first, and a Postgres integration test covers the empty-inbox case.
- Peer signal exchange now carries and checks provenance (#198): exported signals include a stable `signal_id`, `origin`, the block's `expires_at_utc`, a `hops` count and an HMAC `signature` (new `PeerSync:NodeId` and `PeerSync:SigningKey`); only locally originated decisions are exported (never anything imported from a peer or community list); importing requires a valid signature from the configured peer (`PeerSync:Peers[].VerificationKey`) with a matching origin, rejects expired signals, replays, loops and signals over `PeerSync:MaximumHops` (default 0), and keeps the signal's own expiry (capped at the local block lifetime). Peers stay `ObserveOnly` unless set to `BlockList`. The export field names are now snake_case, which is what the .NET peer client already read: before this change a .NET node's export used camelCase and a .NET importer silently ignored it. **Upgrade note:** `PeerSync:RequireSignedSignals` defaults to true, so peers must be given a signing key and verification key, or set it to false (also needed for legacy plain-IP feeds), or imports stop.
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
