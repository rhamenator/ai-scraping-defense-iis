# Escalation Scoring and Provider Baseline

This document defines the post-v1 baseline for expanding escalation scoring, abuse-intelligence provider coverage, and optional model adapters.

## Objectives

- improve score composition with clearer operator-visible rationale
- support additional reputation and abuse-intelligence providers
- keep model adapters optional so default deployments stay lightweight
- preserve deterministic fallback behavior when optional dependencies are disabled

## Scope

### Provider expansion

- add normalized provider adapters behind existing service abstractions
- define per-provider timeout, retry, and disable controls
- record provider contribution metadata for operator visibility

### Score composition

- define weighted score components with stable ranges
- expose component-level rationale in operator-facing event data
- support policy thresholds for action levels (observe, challenge, block, tarpit)

### Optional model adapters

- support OpenAI-compatible and local model adapters as optional integrations
- require explicit configuration to enable model-assisted enrichment
- guarantee non-model fallback remains fully supported

## Validation Requirements

- unit tests for each scoring component and provider adapter boundary
- integration tests for mixed provider availability and timeout paths
- explicit tests for model-disabled and model-enabled configurations
- operator API assertions for score rationale visibility

## Operational Guidance

- default deployments should run without external model dependencies
- enable provider/model integrations incrementally with rollback controls
- monitor provider latency and error rates before increasing scoring weight
