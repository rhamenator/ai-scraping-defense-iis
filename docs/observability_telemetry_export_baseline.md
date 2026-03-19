# Observability and Telemetry Export Baseline

This document defines the post-v1 baseline for structured metrics, trace-friendly instrumentation, and richer operator telemetry export.

## Objectives

- publish structured application and defense metrics
- add trace correlation across key request and defense workflows
- improve operator-visible telemetry for diagnosis and auditability
- validate telemetry behavior in packaged deployments

## Scope

### Structured metrics

- define stable metric names, labels, and cardinality limits
- publish request path, defense action, and error-class metrics
- preserve low-overhead defaults for production safety

### Trace instrumentation

- instrument ingress, escalation, tarpit, management, and sync workflows
- propagate correlation identifiers across internal boundaries
- support standards-based export to tracing backends

### Operator telemetry

- include correlation IDs in operator event views
- expose richer timeline fields for investigations
- preserve API-first telemetry access patterns for dashboard consumers

## Validation Requirements

- unit/integration tests for metrics emission and label stability
- tests for trace propagation across core workflow boundaries
- packaged deployment checks for telemetry endpoint behavior
- tests ensuring telemetry remains functional when optional backends are disabled

## Operational Guidance

- set explicit metric label and sampling guardrails before broad enablement
- stage trace export rollouts to prevent ingestion overload
- treat telemetry schema changes as versioned contract updates
