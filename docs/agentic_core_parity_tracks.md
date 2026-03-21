# Agentic-Core Parity Implementation Tracks

This document breaks the .NET parity work into explicit implementation tracks so contributors can pick up non-overlapping workstreams without reopening the same core assessment surfaces in parallel.

Commercial v1 is already shipped. These tracks describe the parity-first sequence that should land before broader "more agentic" orchestration work begins.

## Track Overview

| Track | Focus | Issue-backed scope | Dependency summary | Status |
| --- | --- | --- | --- | --- |
| Track A | Orchestration and containment foundation | #102, #99, #105 | No downstream track should start until the routing, adapter/provider contracts, and containment policy seams are stable. | Complete |
| Track B | Extensibility and learning loop | #100, #103 | Depends on Track A so contributor contracts and decision-memory hooks land on stable routing/containment primitives. | Complete |
| Track C | Operator recommendation and explainability workflows | #101, #104 | Depends on Track A for durable assessment metadata. Can overlap late Track B work once contributor outputs are stable. | Complete |

## Track A — Orchestration and Containment Foundation

Goal: make the queued assessment path predictable enough that later extensibility and operator-facing work do not need to re-litigate core execution flow.

Included issues:

- #102 — broaden .NET model adapter and provider contract
- #99 — add model routing layer for escalation classifiers
- #105 — add configurable containment policy engine

Why this track lands first:

- adapter/provider contracts define the extension seams used by later parity work
- routing determines which model paths execute and what telemetry surfaces exist
- containment policy establishes the final decision boundary that operator tooling must explain

Exit criteria before moving on:

- routing and containment behavior are configuration-driven
- model/provider integration points no longer require core-engine rewrites for each new capability
- follow-on tracks can depend on stable assessment and decision shapes

## Track B — Extensibility and Learning Loop

Goal: add the composable assessment surfaces that let the .NET stack evolve beyond built-in heuristics without turning every new idea into a `ThreatAssessmentService` rewrite.

Included issues:

- #100 — add extensible threat-scoring contributor pipeline
- #103 — add decision memory and operator feedback hooks

Dependency notes:

- Track B assumes Track A already stabilized routing, provider seams, and containment decisions
- decision memory is most useful after contributor outputs are explicit and attributable

Parallelization guidance:

- contributor-pipeline work should land before broadening memory/feedback persistence around those contributors
- once contributor naming and breakdown semantics are stable, memory and feedback can proceed without touching routing internals

## Track C — Operator Recommendation and Explainability Workflows

Goal: turn the underlying assessment and feedback signals into operator-facing workflows that improve tuning, trust, and day-to-day triage.

Included issues:

- #101 — add operator tuning recommendation service
- #104 — enrich escalation explainability and telemetry

Dependency notes:

- explainability depends on Track A producing stable routing/containment metadata
- operator recommendations become materially more useful once contributor outputs and decision feedback from Track B are available

Parallelization guidance:

- explainability/telemetry can start as soon as routing and containment metadata exist
- recommendation logic should consume the same persisted breakdowns and operator feedback instead of introducing a separate decision model

## Parity-First Sequence Before Broader Agentic Work

The intended order is:

1. Finish Track A so orchestration, routing, and containment are stable.
2. Finish Track B so extensibility and operator feedback can ride on those stable seams.
3. Finish Track C so operators can inspect, tune, and trust the resulting decision flow.
4. Only then start broader agentic-core work such as more autonomous orchestration, deeper plugin behavior, or higher-level recommendation automation.

This sequence keeps the "more agentic" phase from being forced to solve basic routing, containment, and explainability gaps at the same time.

## Current Repository Status

Tracks A, B, and C are now implemented. The practical implication is that future agentic-core proposals should treat these parity surfaces as the baseline rather than reopening them as hidden prerequisites.

If new post-parity work is proposed, it should:

- identify which track-owned contracts it depends on
- avoid overlapping changes across routing, contributor, and operator-workflow surfaces in the same PR unless required
- reference this document from the issue or design note so sequencing remains explicit