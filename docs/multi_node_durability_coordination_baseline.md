# Multi-Node Durability and Coordination Baseline

This document defines the post-v1 baseline for supported multi-node durability and coordination behavior.

## Objectives

- define supported multi-node topologies and trust boundaries
- close durability and consistency gaps for shared state and audit data
- document coordination guarantees, conflict behavior, and failure modes
- provide validation coverage for supported multi-node operation

## Supported Topology Targets

- active-active edge nodes with shared Redis and durable event storage
- active-passive failover with deterministic role transition controls
- bounded peer-coordination overlays with explicit trust configuration

## Durability and Consistency Baseline

- define authoritative stores for hot state vs. durable audit/event data
- define write/read consistency expectations per data class
- define replay/recovery behavior after partial failures
- define idempotency requirements for cross-node signal processing

## Coordination Guarantees

- bounded eventual consistency for non-critical shared signals
- explicit conflict resolution strategy for concurrent updates
- deterministic behavior when peers are partitioned or stale
- operator-visible state for replication lag and conflict events

## Validation Requirements

- integration tests for multi-node write/read and failover behavior
- chaos-style tests for partition, stale-peer, and recovery scenarios
- tests for idempotency and duplicate-signal handling across nodes
- operator/API checks for lag, conflict, and recovery telemetry

## Operational Guidance

- start with single-node baseline and promote to multi-node with staged rollout
- enforce explicit node identity, trust, and authentication controls
- define runbook steps for failover, rejoin, and conflict remediation
