# Community Blocklist and Peer Coordination Baseline

This document defines the post-v1 baseline for strengthening community blocklist trust policy and peer coordination behavior.

## Objectives

- improve source trust policy for imported blocklist and peer signals
- strengthen deduplication, attribution, and reporting controls
- improve peer trust scoring and coordination behavior in multi-node exchanges
- increase operator visibility into accept/reject decisions

## Scope

### Source trust policy

- define trust tiers for feed and peer sources
- support explicit allowlist/denylist and weighted trust profiles
- require signed/authenticated source metadata where available

### Deduplication and reporting

- define canonical identity keys and merge policy for duplicate signals
- preserve source attribution and confidence when consolidating entries
- provide operator-facing reports for accepted, downgraded, and rejected signals

### Peer coordination

- score peer sources on reliability, freshness, and historical quality
- define bounded propagation policies to prevent feedback loops
- define safe defaults when peer trust score is low or stale

## Validation Requirements

- tests for trust-tier policy and source classification paths
- tests for deduplication merge and conflict resolution behavior
- tests for peer-score changes and propagation guardrails
- operator API tests for decision transparency fields

## Operational Guidance

- start with conservative trust profiles and expand intentionally
- review rejection and downgrade trends before changing trust thresholds
- keep peer coordination behavior observable and reversible
