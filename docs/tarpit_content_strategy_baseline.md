# Tarpit Content Strategy Baseline

This document defines the post-v1 baseline for expanding tarpit content strategy beyond current deterministic variants and PostgreSQL-backed Markov content.

## Objectives

- increase crawl-wasting depth while preserving deterministic operator controls
- add richer content-generation sources and render variants
- improve streaming and archive-rotation behavior for large tarpit corpora
- document storage/runtime implications and safe operational defaults

## Scope

### Content sources and variants

- support layered content sources (deterministic templates, Markov, curated archives)
- add configurable render modes by route or policy profile
- retain explicit controls for allowed content classes and response size limits

### Streaming and archive rotation

- define bounded streaming response policies
- define archive retention and rotation windows with storage caps
- provide fallback behavior when archive sources are unavailable

### Operator controls

- expose selected tarpit mode and source metadata in operator events
- support runtime toggles for conservative and aggressive tarpit profiles
- preserve deterministic mode for predictable investigations and tests

## Validation Requirements

- tests for each tarpit mode and source selection path
- tests for archive rotation and missing-source fallback behavior
- tests for bounded response size/time guarantees
- deployment checks for storage and PostgreSQL impact

## Operational Guidance

- start with deterministic mode and add richer sources incrementally
- enforce explicit CPU, memory, and response-time budgets
- monitor operator metrics for generation latency and fallback rates
