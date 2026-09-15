# Implementation Plan

**Phase 1 is AUTHORIZED** — `docs/10-human-review/v0.3/HUMAN_APPROVAL.md` records
`APPROVED_WITH_CONDITIONS` with all conditions satisfied (2026-09-15).

**It has not started.** Authorization removes a prohibition; it does not issue a task. Neither Claude
nor Codex may begin implementation on their own initiative, on consensus, or because the gate is
open. Work begins when the product owner asks for it.

> v0.3 expands the roadmap from 8 phases to **13** (Phase 0–12). v0.2 bundled observability, MLflow,
> Kubernetes, and the dashboard into two large phases, which hid their dependencies and made "done"
> unmeasurable per phase.

## Phase 0 — Documentation baseline — **COMPLETE, APPROVED**
- [x] resolve all open decisions (9 Decision Records, 3 dual-agent rounds)
- [x] validate requirement/AC coverage (44 AC; every P0 mapped or explicitly deferred)
- [x] finalize contracts and reason codes (8 schemas, proto, OpenAPI; contract tests passing)
- [x] simulator physics, OT address maps, state machines, failure matrix, 12 service designs
- [x] **human approves the architecture** — APPROVED_WITH_CONDITIONS, all conditions satisfied (2026-09-15)

## Phase 1 — Equipment Simulator / HIL — **AUTHORIZED, NOT STARTED**
Equipment physics and degradation model · equipment state machine (T1–T12) · 10 fault profiles ·
**L3 self-protection**: protective trips and dead-man revert · deterministic seeding.
**Proof:** AC-001, AC-018, AC-019, AC-020.
**Depends on:** nothing. This is why it is first.

## Phase 2 — Edge Gateway + OPC UA / Modbus
Protocol server endpoints per `OT_PROTOCOL_MAPPING.md` · gateway client sessions · normalisation ·
closed quality vocabulary and derived `quality.overall` · reconnect without process restart ·
bounded buffering with drop-oldest.
**Proof:** AC-002, AC-021, AC-022, AC-023.
**Depends on:** Phase 1 (nothing to read otherwise).

## Phase 3 — Kafka Event Backbone
7 topics with pinned partitions, retention, compaction · idempotent producers · manual offset
commit · DLQ and redrive · lag and record-age metrics · replay on projector groups only.
**Proof:** AC-003, AC-026, AC-027.

## Phase 4 — Operational Data + Operations API
PostgreSQL projections and read models · rebuild-from-Kafka · Operations API per the OpenAPI
contract · correlation-chain retrieval.
**Proof:** AC-006, AC-028, AC-029, AC-030.

## Phase 5 — AI Training + Inference
Reproducible dataset generation · `mair_ml_core` shared features · anomaly and failure/RUL
baselines · MLflow experiments and registry · prediction service with `deploymentStage` and
`validSampleRatio` · deterministic null aggregation.
**Proof:** AC-008, AC-024, AC-025, AC-032.

## Phase 6 — AI Safety Supervisor
13 canonical gates in order · full `gateResults` recording · reason codes · control lease and
heartbeat · `seekToEnd` restart semantics · fallback requests.
**Proof:** AC-005, AC-015, AC-016, AC-017.
**Note:** AC-011 (Supervisor death) **cannot be demonstrated until Phase 7 delivers the watchdog.**

## Phase 7 — Control Service
Sole application equipment writer · mTLS identity and `authenticatedSource` · `controlEpoch`
fencing and lease issuance · expiry, supersession, bounds, rate budget, durable idempotency ·
**fallback watchdog** · control mode ownership and persistence.
**Proof:** AC-004, AC-007, AC-011, AC-012, AC-013, AC-014, AC-038, AC-040.
**This phase closes the reliability claim.** Until it ships, ADR-0011 is documented but unproven.

## Phase 8 — Observability
OTel instrumentation · the metrics contract with bounded label sets · Prometheus and Grafana ·
alert rules · trace continuity across all four hops.
**Proof:** AC-035, AC-036, AC-041, AC-044.

## Phase 9 — MLflow / Model Lifecycle
`mlops-publisher` · `ModelAuthorization` on the compacted topic · 30 s watermark and 90 s timeout ·
quarantine durability before registry transition reports complete · promotion gates.
**Proof:** AC-033, AC-034.

## Phase 10 — Kubernetes production-like deployment
Manifests and Helm · probes wired to real readiness semantics · rollout and rollback ·
RF=3 / `min.insync=2` · mTLS everywhere · restart and recovery demonstrations.
**Proof:** AC-043 plus re-running the Phase 7 failure tests under orchestration.

## Phase 11 — Operations Dashboard
Live equipment, AI, control-mode, and platform-health views · **active rejection/fallback reason
displayed**, not just a boolean · staleness banner · demo-only fault injection, absent outside demo.
**Proof:** AC-009, AC-031, AC-037.

## Phase 12 — Failure injection, reliability, load
All 13 failure-test specifications executed and reported · load profiles at 20 and 250 equipment ·
**every `TARGET (unmeasured)` replaced by a measured value with environment metadata**, or explicitly
left unmeasured.
**Proof:** AC-010, AC-042, and the NFR §2 target table.
**Until this phase completes, no performance figure in this repository may be presented as a result.**

---

## Phase ordering — challenged and retained

Codex was asked to challenge this ordering. It is retained, with the dependencies made explicit:

| Constraint | Why |
|---|---|
| 1 → 2 | the gateway has nothing to read until the simulator serves protocol endpoints |
| 2 → 3 | the backbone has nothing to carry until telemetry is normalised |
| 3 → 5 | features come from the stream |
| 5 → 6 | gates have nothing to evaluate without predictions |
| **6 → 7** | the Supervisor must exist before the component that fences it |
| 7 → 10 | orchestration should wrap a correct system, not hide an incorrect one (ADR-0005) |
| 9 before 12 | rollback cannot be demonstrated before the lifecycle exists |

**The one uncomfortable dependency, stated rather than reordered:** AC-011 and AC-012 — the tests
that prove the platform's headline claim — cannot run until **Phase 7**. Moving the Control Service
watchdog earlier was considered and rejected: it would put the equipment-write component ahead of
the safety component, inverting the authority story the project exists to tell. The cost is that the
reliability claim stays documented-but-unproven for six phases, and that is recorded here so it is
not mistaken for an oversight.
