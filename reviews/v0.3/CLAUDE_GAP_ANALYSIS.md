# Claude Independent Gap Analysis — Specification v0.2

> Author: Claude Code (Primary Engineer / Primary Architect)
> Date: 2026-09-15
> Method: Full read of MASTER_SPEC.md, AGENTS.md, CLAUDE.md, README.md, CHANGELOG.md,
> all of `docs/**`, all of `contracts/**`.
> Written **before** reading Codex's independent analysis, so that the reconciliation in
> `reviews/v0.3/RECONCILED_GAP_ANALYSIS.md` is an honest comparison of two independent passes.

## Severity definitions

| Severity | Meaning |
|---|---|
| **BLOCKER** | Phase 1 cannot start. An implementer is forced to invent architecture, or a safety/authority hole exists. |
| **HIGH** | Implementation possible but will produce wrong, unverifiable, or unsafe behavior. Must be fixed for v0.3. |
| **MEDIUM** | Ambiguity that causes rework or untestable claims. Should be fixed for v0.3. |
| **LOW** | Polish, consistency, or future-facing. May be deferred with a note. |

Counts: **BLOCKER 13 · HIGH 28 · MEDIUM 15 · LOW 3 — total 59**

---

## A. Contracts

### CG-001 — BLOCKER — Telemetry doc example contradicts the telemetry JSON Schema
**Files:** `docs/03-contracts/EVENT_CONTRACTS.md`, `contracts/jsonschema/v1/telemetry.schema.json`

The JSON Schema requires **seven** measurements — `temperatureC, vibrationRms, currentA, voltageV, rpm, torqueNm, operationRatePct` — and sets `"additionalProperties": false` with all seven in `required`. The documented example in `EVENT_CONTRACTS.md` carries only **four**: `temperatureC, vibrationRms, currentA, rpm`.

The canonical example in the higher-read-order document **would fail validation against its own machine-readable contract**.

**Why it blocks:** `REPOSITORY_STRUCTURE.md` declares `contracts/` the machine-readable source of truth and forbids hand-edited DTO divergence. Two normative artifacts disagree, so neither can be trusted to generate DTOs or contract tests.
**Resolution:** Make the doc example a *verbatim* valid instance of the schema; add a contract test that validates every documented example against its schema.

### CG-002 — BLOCKER — Only 1 of 4+ event types has a machine-readable schema
**Files:** `contracts/jsonschema/v1/` (only `telemetry.schema.json` exists)

`factory.predictions.v1`, `factory.safety-decisions.v1`, and command-outcome events exist only as prose examples in `EVENT_CONTRACTS.md` / `API_AND_COMMAND_CONTRACTS.md`.

**Why it blocks:** Contract tests (TEST_STRATEGY §2) and cross-language DTO generation are impossible for 3 of 4 event families.
**Resolution:** Author JSON Schemas for prediction, safety-decision, command-outcome, equipment-state-change, plus a shared envelope schema.

### CG-003 — BLOCKER — No common event envelope; per-event metadata is inconsistent
**File:** `docs/03-contracts/EVENT_CONTRACTS.md`

- telemetry: has `eventId`, `schemaVersion`, `sequence`, `ingestTimeUtc`
- prediction: has `predictionId` but **no** `eventId`, **no** `schemaVersion`, **no** `ingestTimeUtc`, **no** `sequence`
- safety-decision: has `decisionId` but **no** `schemaVersion`, **no** `sequence`

There is no `eventType` and no `causationId` anywhere.

**Why it blocks:** Consumers cannot uniformly deduplicate, version-gate, or build a causation chain. NFR-002 traceability is asserted but not mechanically supported.
**Resolution:** Define one envelope (`eventId, eventType, schemaVersion, equipmentId, occurredAtUtc, ingestTimeUtc, sequence, correlationId, causationId, producer`) and nest a typed `payload`.

### CG-004 — BLOCKER — Control command contract cannot detect a *stale* or *reordered* command
**Files:** `contracts/proto/control/v1/control.proto`, `docs/03-contracts/API_AND_COMMAND_CONTRACTS.md`

`ApplyCommandRequest` carries `idempotency_key` and `expires_at_utc` only. There is **no issue timestamp, no monotonic sequence, and no decision linkage**.

Two commands issued at T1 and T2 (T2 > T1), both still unexpired, that arrive out of order will both be applied — the **older intent wins** because ordering is decided by arrival, not by intent time.

**Why it blocks:** This is a direct equipment-control correctness hole; the v0.2 documents claim reordering safety they do not implement.
**Resolution:** Add `issued_at_utc`, a per-equipment monotonic `intent_sequence`, and `decision_id`; require Control Service to reject any command whose `intent_sequence` is at or below the last applied for that equipment (`COMMAND_SUPERSEDED`).

### CG-005 — HIGH — OpenAPI contract is a non-implementable stub
**File:** `contracts/openapi/operations-api-v1.yaml`

Every response is `{description: OK}`. No `components/schemas`, no error model, no pagination, no auth scheme, no time-range query parameters for the decisions endpoint.

**Why it blocks:** The dashboard (FR-050/051) and AC-006/AC-009 cannot be implemented or contract-tested.
**Resolution:** Fully specify schemas, errors (RFC 9457 problem details), pagination, filters, and a security scheme.

### CG-006 — HIGH — Proto uses bare `string` for timestamps
**File:** `contracts/proto/control/v1/control.proto` (`expires_at_utc`, `applied_at_utc`)

**Why it blocks:** Untyped timestamps invite format/parse/skew divergence and defeat generated-code validation.
**Resolution:** Use `google.protobuf.Timestamp`, or state ISO-8601 UTC with explicit strict parsing and reject-on-malformed behavior.

### CG-007 — HIGH — No DLQ / poison-message policy exists
**File:** `ADR-0001` names the obligation; no document discharges it.

ADR-0001 "Consequences" says retry/DLQ rules must be designed. No document defines a DLQ topic, redrive policy, poison-message threshold, or what a consumer does with an unparseable event.

**Resolution:** Define per-topic DLQ naming, max delivery attempts, and redrive procedure.

### CG-008 — HIGH — Two Kafka topics required by the architecture are undefined
**Files:** `ADR-0009`, `docs/02-architecture/SYSTEM_ARCHITECTURE.md`

ADR-0009 states Control Service "publishes command outcomes to Kafka for audit". `SYSTEM_ARCHITECTURE` §7 states Operations Service builds "audit read models" by projecting from Kafka. **No command-outcome topic and no equipment-state-change topic are defined anywhere.**

**Resolution:** Define `factory.control-outcome.v1` and `factory.equipment-state.v1` with full topic specs.

### CG-009 — MEDIUM — No runtime schema-validation or registry decision
**File:** `ADR-0006`

ADR-0006 defers a schema registry, but nothing states whether consumers validate against the JSON Schema at runtime, where schemas are loaded from, or what happens on `schemaVersion` mismatch.

**Resolution:** State the baseline: generated DTOs plus startup-loaded schemas plus reject-unknown-major-version, with registry deferred by ADR.

---

## B. Safety, control authority, state

### CG-010 — BLOCKER — "Safety Supervisor unavailable" has no defined behavior anywhere
**Files:** `MASTER_SPEC.md` §7 gate 9, `docs/02-architecture/FAILURE_MODEL.md`

MASTER_SPEC lists "Safety Supervisor itself is healthy" as gate 9 — a gate the Supervisor evaluates **about itself**. If the Supervisor process is down, nothing evaluates it. `FAILURE_MODEL.md` has **no row** for Supervisor unavailability.

**Why it blocks:** The most safety-relevant failure in the system has no specified behavior.
**Resolution:** Define it explicitly, including who drives equipment to fallback in the Supervisor's absence (see CG-011).

### CG-011 — BLOCKER — "Control survives AI failure" is only true for *inference* failure, not *Supervisor* failure
**Files:** `MASTER_SPEC.md` principle 3 and §6, `NFR-001`, `AC-004`

The only component that issues commands is the Safety Supervisor. "Fallback" (`SAFE_FALLBACK`, 60%) is defined as a **mode of the Supervisor**, not as an autonomous property of the Control Service or the equipment.

Therefore: kill the Python inference service — the Supervisor still runs, rejects AI, and commands fallback, so the claim holds and AC-004 passes.
Kill the Supervisor — **nothing commands anything**; equipment holds its last commanded rate indefinitely, which may be an AI-elevated rate.

**Why it blocks:** The platform's headline reliability claim is not architecturally guaranteed. A reviewer will find this immediately.
**Resolution — needs decision:** give Control Service an independent deterministic watchdog that drives to the fallback rate after a bounded Supervisor-silence timeout, and/or give the equipment/simulator a local dead-man behavior. Architecture-significant: requires an ADR and Codex challenge.

### CG-012 — BLOCKER — Equipment state machine does not exist, yet a reason code depends on it
**Files:** `docs/03-contracts/REASON_CODES.md` (`EQUIPMENT_STATE_INELIGIBLE`), `MASTER_SPEC.md` §7, `docs/04-ai/AI_SAFETY_AND_MLOPS.md` gate 7

No equipment state model appears anywhere in v0.2. Gate 7 "equipment state eligibility" and reason code `EQUIPMENT_STATE_INELIGIBLE` reference a model that is undefined.

**Why it blocks:** A required safety gate cannot be implemented or tested.
**Resolution:** Formalize the equipment state machine with owner, transitions, triggers, timeouts, and safety consequences.

### CG-013 — BLOCKER — Control mode state machine is named but never specified
**File:** `docs/02-architecture/FAILURE_MODEL.md` (Degraded modes)

Four modes are listed with one sentence each. Missing: authoritative owner, who may request a transition, who approves, which transitions are legal, automatic vs operator-driven transitions, operator acknowledgement requirements, exit conditions from `STOP_REQUIRED`, and anti-flapping/hysteresis.

**Why it blocks:** Mode is the core control abstraction; it is currently undefined behavior.
**Resolution:** Full state machine plus Mermaid diagram plus transition table.

### CG-014 — BLOCKER — The two normative safety-gate lists disagree
**Files:** `MASTER_SPEC.md` §7 (9 gates) vs `docs/04-ai/AI_SAFETY_AND_MLOPS.md` (10 gates)

| In MASTER_SPEC only | In AI_SAFETY_AND_MLOPS only |
|---|---|
| "Safety Supervisor itself is healthy" | "required feature completeness" |
| "telemetry freshness is valid" (distinct from quality) | "rate-of-change limit" |
| | "final bounded recommendation" |

The orders also differ. `REASON_CODES.md` contains `FEATURE_INCOMPLETE`, `RATE_CHANGE_LIMITED` **and** `TELEMETRY_STALE`, i.e. it implements the **union**, matching neither list.

**Why it blocks:** MASTER_SPEC is highest authority but is the less complete list; implementing it literally omits two gates that have reason codes.
**Resolution:** Reconcile to one canonical ordered gate list (the union), update MASTER_SPEC, and make REASON_CODES derive from it one-to-one.

### CG-015 — HIGH — `RATE_CHANGE_LIMITED` is contradictory: reject or clamp?
**Files:** `docs/03-contracts/REASON_CODES.md`, `docs/03-contracts/EVENT_CONTRACTS.md`

The name implies **clamping**. Its placement in the AI/Safety rejection list implies **rejection**. The safety-decision contract has `decision: ACCEPT|REJECT|FALLBACK` plus a separate `boundedOperationRatePct`, which implies clamp-then-accept.

**Resolution:** Split the concepts: `ACCEPT` may carry `RATE_CHANGE_CLAMPED` as a non-rejecting modifier code; keep rejection codes strictly rejecting. Define whether a clamped accept is auditable as a modification of AI intent.

### CG-016 — HIGH — Rate-of-change limit has no cumulative bound and no defined baseline
**File:** `docs/02-architecture/RUNTIME_BEHAVIOR.md`

"max rate change per accepted recommendation 10 percentage points" plus inference every 5 s means 100% to 60% in roughly 20 s across four accepted recommendations. No per-window cumulative limit is defined.

Additionally undefined: is the 10 pp measured against the **last commanded** value or the **last acknowledged actual** value? If commanded and a command fails, the baseline silently drifts from reality.

**Resolution:** Add a cumulative rate-change budget per rolling window, and specify the delta baseline as the last *confirmed applied* value.

### CG-017 — HIGH — Escalation to `STOP_REQUIRED` is discretionary ("may")
**File:** `docs/04-ai/AI_SAFETY_AND_MLOPS.md` (Fallback policy)

"fallback **may** escalate to `STOP_REQUIRED`" — no trigger condition, no owner, no operator-acknowledgement rule, no exit path.

**Resolution:** Define deterministic entry conditions, the authority that may command it, and the acknowledged exit path.

### CG-018 — HIGH — Prediction/decision race: multiple predictions are valid simultaneously
**File:** `docs/02-architecture/RUNTIME_BEHAVIOR.md` (inference every 5 s, prediction TTL 10 s)

Two or three predictions per equipment are simultaneously within TTL. No rule requires the Supervisor to act only on the **newest** prediction per equipment, and no monotonicity rule on `predictedAtUtc` is stated. After a consumer restart or replay, an older prediction can be processed after a newer one.

**Resolution:** Require per-equipment monotonic prediction processing; discard any prediction older than the last processed `predictedAtUtc`; emit `PREDICTION_SUPERSEDED`.

### CG-019 — BLOCKER — Kafka replay of predictions would re-issue real commands
**Files:** `AC-003`, `docs/03-contracts/API_AND_COMMAND_CONTRACTS.md`, `ADR-0001`

The Safety Supervisor consumes predictions from Kafka. On replay it re-evaluates historical predictions and emits **new** command intents. The idempotency key is `equipmentId:decisionId`, and a replay produces a **new** `decisionId`, hence a **new** idempotency key, so the command **is applied again**.

AC-003 claims "stateful consumers do not duplicate command effects" — the current design does not deliver that.

**Why it blocks:** Replay is an advertised portfolio demonstration (`FAIL-KAFKA-001`) and would, as designed, move real equipment based on stale history.
**Resolution — needs decision:** either (a) the Supervisor is not replay-eligible and replay is confined to projection/analytics consumer groups, or (b) commands carry the originating prediction's `predictedAtUtc` and Control Service rejects any intent whose source prediction is older than the freshness bound. Requires ADR plus Codex challenge.

### CG-020 — HIGH — Idempotency key only protects transport retry, and that limit is not stated
**Files:** `docs/03-contracts/API_AND_COMMAND_CONTRACTS.md`, `AC-007`, `AC-003`

Because `decisionId` is fresh per evaluation, `equipmentId:decisionId` dedupes **gRPC retries of one intent only**. AC-007 tests exactly that. AC-003 implies broader logical protection the design does not provide (see CG-019). The scope limit is nowhere documented.

**Resolution:** State the guarantee precisely: idempotency protects transport-level redelivery of a single decision, not logical re-decision.

### CG-021 — HIGH — Idempotency store has no defined storage, TTL, or restart behavior
**Files:** `docs/02-architecture/RUNTIME_BEHAVIOR.md` (Persistence), `API_AND_COMMAND_CONTRACTS.md`

"Duplicate idempotency keys return prior result without repeating effect" — but where the record lives, how long it is retained, and whether dedup survives a Control Service restart are all undefined.

**Resolution:** Specify durable dedup in PostgreSQL, retention at least command TTL times a safety factor, and dedup-on-restart as mandatory.

### CG-022 — HIGH — Control Service behavior on protocol write failure is undefined
**File:** `docs/03-contracts/REASON_CODES.md` (`COMMAND_PROTOCOL_FAILED`)

Does it retry? How many times? Can a write be partially applied? Is `SET_OPERATION_RATE` idempotent at the OT layer (it is naturally, but this is never stated, and that property is what makes retry safe)?

**Resolution:** State OT-level idempotency of the setpoint write, bounded retry with backoff, and terminal behavior.

### CG-023 — BLOCKER — The `source` field is self-asserted and unverified
**Files:** `contracts/proto/control/v1/control.proto` (`string source`), `docs/03-contracts/API_AND_COMMAND_CONTRACTS.md`

"Only `SAFETY_SUPERVISOR` … may originate a production command path" is enforced by a **string the caller sets itself**. Any process able to reach the Control Service gRPC port can claim to be the Supervisor.

**Why it blocks:** This is a control-authority bypass — precisely the property ADR-0002 exists to guarantee.
**Resolution:** Authenticate the caller (mTLS service identity) and derive `source` from the verified peer identity, never from the payload.

### CG-024 — MEDIUM — Fault injection is an equipment write that bypasses Control Service
**Files:** `docs/02-architecture/SYSTEM_ARCHITECTURE.md` §8, `docs/02-architecture/SECURITY_BOUNDARIES.md`

SECURITY_BOUNDARIES: "only Control Service may perform equipment writes." SYSTEM_ARCHITECTURE: the dashboard routes fault injection "through guarded Operations Service endpoints to simulator admin APIs" — a write that reaches the simulator without passing Control Service.

**Resolution:** Add an explicit carve-out distinguishing **simulator-admin writes** (fault injection, demo profile only) from **equipment-control writes** (Control Service exclusive). Without it this reads as a UI control bypass.

---

## C. Distributed systems / Kafka

### CG-025 — HIGH — No partition count, replication factor, or retention is specified for any topic
**Files:** `docs/08-roadmap/OPEN_DECISIONS.md` #7, `docs/03-contracts/EVENT_CONTRACTS.md`

"one KRaft broker for local" is the only topology statement. Phase 2 cannot create topics.

**Resolution:** Per-topic table: partitions, RF, `min.insync.replicas`, retention, cleanup policy, and local-vs-production values.

### CG-026 — HIGH — Per-equipment ordering is asserted without the conditions that make it true
**File:** `ADR-0001`

"partitioning by equipment preserves per-equipment order" is only true if partition count never changes, the producer is idempotent, and `max.in.flight.requests.per.connection` is constrained with retries enabled. None are stated.

**Resolution:** Declare partition count immutable for v1 topics; mandate `enable.idempotence=true` and the corresponding in-flight limit; state that a partition-count change requires a new topic version.

### CG-027 — HIGH — `sequence` semantics are undefined
**File:** `contracts/jsonschema/v1/telemetry.schema.json`

`sequence: integer, minimum 0` — per equipment or per gateway? Monotonic across gateway restart? Does it reset to 0? What is a gap, and what must a consumer do about one?

**Why it blocks:** Loss detection and the sequence-gap data-quality concept are unimplementable.
**Resolution:** Define as strictly monotonic per `equipmentId`, persisted across restart, with an explicit gap-detection and quality-flagging rule.

### CG-028 — HIGH — Gateway buffer bound and overflow policy are undefined
**Files:** `docs/05-operations/OBSERVABILITY_AND_SLO.md` (`local_buffer_depth`), `docs/06-development/CODING_STANDARDS.md` (bounded `Channel<T>`)

The bound is required to be bounded but its value and the **drop policy on overflow** (drop oldest / drop newest / block the protocol poll / degrade cadence) are unspecified. `FAILURE_MODEL.md` has no buffer-overflow row.

**Why it blocks:** Determines whether telemetry loss is bounded and whether a Kafka outage stalls OT polling — a control-adjacent risk.
**Resolution:** Specify capacity, drop policy, the quality flag raised on drop, and the metric/alert.

### CG-029 — MEDIUM — Consumer group naming convention is undefined
**File:** `ADR-0010` ("an independent shadow consumer group")

### CG-030 — MEDIUM — Consumer lag has no threshold or alert rule
**Files:** `FR-012`, `docs/05-operations/OBSERVABILITY_AND_SLO.md`

---

## D. Time semantics and data quality

### CG-031 — HIGH — No clock model, no skew tolerance, no clock-source statement
**Files:** `docs/02-architecture/RUNTIME_BEHAVIOR.md`, `docs/06-development/CODING_STANDARDS.md`

The 2 s telemetry freshness gate and 10 s prediction TTL compare timestamps produced on one host against a clock read on another. No NTP requirement, no skew budget, no statement of which component is authoritative for `eventTimeUtc` (simulator or gateway), and no monotonic-clock guidance for measuring elapsed time.

**Why it blocks:** Under Kubernetes (Phase 10) a modest skew silently converts every gate into a false reject or, worse, a false accept.
**Resolution:** Define the clock source per timestamp field, a skew budget, NTP as an operational precondition, and mandate monotonic clocks for interval measurement.

### CG-032 — HIGH — Timestamp reversal (`eventTimeUtc` after `ingestTimeUtc`) has no defined handling
**File:** `contracts/jsonschema/v1/telemetry.schema.json`
The schema permits it. No quality flag, no reason code, no consumer behavior.

### CG-033 — BLOCKER — `quality.flags` is an unconstrained string array, so the quality gate is unimplementable
**Files:** `contracts/jsonschema/v1/telemetry.schema.json`, `MASTER_SPEC.md` §7 gate 2

`flags: {"type":"array","items":{"type":"string"}}` — no enum. The safety gate requires "required sensors have **acceptable** data-quality flags", but neither the flag vocabulary nor "acceptable" is defined.

**Why it blocks:** A P0 safety gate has no decidable input domain, and metric label cardinality is unbounded, contradicting REASON_CODES.md's own low-cardinality rule.
**Resolution:** Enumerate the closed flag vocabulary (missing, NaN, out-of-range, stale, disconnected, frozen, outlier, timestamp-reversed, duplicate, sequence-gap), map each to `overall`, and define per-gate acceptability.

### CG-034 — HIGH — A dead sensor has no legal representation in the contract
**File:** `contracts/jsonschema/v1/telemetry.schema.json`

All seven measurements are `required` and `additionalProperties:false`; JSON has no `NaN`. A disconnected or failed sensor therefore cannot be encoded at all — the producer must either fabricate a value or emit an invalid event.

**Why it blocks:** Directly contradicts the requirement to handle missing/NaN sensor data, and fabricating a value would feed the safety gates a lie.
**Resolution:** Allow `["number","null"]` for measurements with a mandatory corresponding quality flag, and forbid substituting a synthetic value.

### CG-035 — HIGH — "Required sensors" per gate / per model is never mapped
**Files:** `MASTER_SPEC.md` §7, `REASON_CODES.md` (`FEATURE_INCOMPLETE`)
No mapping of model to required features to required sensors exists, so feature completeness is undecidable.

---

## E. AI / MLOps

### CG-036 — BLOCKER — `deploymentStage` is mandated by ADR-0010 but absent from the prediction contract
**Files:** `ADR-0010`, `docs/03-contracts/EVENT_CONTRACTS.md`

ADR-0010 states "Prediction events identify `deploymentStage` and model version" and that the Supervisor "accepts only the authorized stage/cohort". The prediction payload has `modelName`/`modelVersion` and **no** `deploymentStage`.

**Why it blocks:** A shadow or canary model's output is indistinguishable from a Production model's at the decision point — a silent path to unauthorized control influence.
**Resolution:** Add `deploymentStage` (SHADOW|CANARY|PRODUCTION) and `modelRunId` to the prediction contract; Supervisor rejects non-authorized stages with a stable reason code.

### CG-037 — HIGH — Feature schema version is not carried on predictions
**Files:** `docs/04-ai/AI_SAFETY_AND_MLOPS.md`, `EVENT_CONTRACTS.md`
`featureWindowId` is present but conveys no schema identity, so training-serving skew is undetectable at decision time.

### CG-038 — HIGH — Model rollback and quarantine mechanics are undefined
**Files:** `FR-043`, `AC-010`, `REASON_CODES.md` (`MODEL_QUARANTINED`), `ADR-0008`

Nothing defines who sets quarantine, where that state lives, or how the Supervisor learns of it. ADR-0008 requires graceful degradation when MLflow is unavailable — but if a model is quarantined **during** an MLflow outage, a degraded Supervisor keeps trusting it. That is fail-open in the safety-relevant direction.

**Resolution:** Define an authoritative, locally-cached model-authorization record with an explicit staleness bound, and make the safety-relevant direction fail *closed*.

### CG-039 — MEDIUM — Canary cohort storage and change control are undefined
**File:** `ADR-0010`

### CG-040 — MEDIUM — Inference latency budget is referenced but never given a value
**File:** `docs/04-ai/AI_SAFETY_AND_MLOPS.md` (Promotion gate)

### CG-041 — MEDIUM — Shadow-vs-production comparison metric and pass threshold are undefined
**File:** `ADR-0010`

---

## F. Non-functional targets and observability

### CG-042 — HIGH — NFR document contains no measurable targets; SLO document contains different ones
**Files:** `docs/01-requirements/NON_FUNCTIONAL_REQUIREMENTS.md`, `docs/05-operations/OBSERVABILITY_AND_SLO.md`

NFR-011 declares targets are hypotheses and gives none. The SLO document independently states five. Neither cross-references the other, and there are no targets at all for telemetry throughput, ingestion latency, inference latency, command latency, recovery time, or acceptable data loss.

**Resolution:** Single normative NFR target table, every row labelled `TARGET (unmeasured)`, with the SLO document deriving from it.

### CG-043 — MEDIUM — Metric label cardinality guidance conflicts with traceability needs
**File:** `docs/05-operations/OBSERVABILITY_AND_SLO.md`
"Avoid high-cardinality metric labels" vs. the need for per-equipment correlation across 20-250 equipment. Never resolved into a rule.
**Resolution:** State that `equipmentId` belongs in traces/logs/exemplars, not in metric labels; enumerate the allowed label sets.

### CG-044 — MEDIUM — Trace sampling strategy has no concrete rate
**File:** `docs/05-operations/OBSERVABILITY_AND_SLO.md` (Tracing)

### CG-045 — LOW — `model_version_info` as a labelled gauge is a cardinality growth risk over time

---

## G. Security

### CG-046 — HIGH — No authentication or authorization mechanism is named anywhere
**File:** `docs/02-architecture/SECURITY_BOUNDARIES.md`

The document states goals but names no mechanism for any hop: Supervisor to Control (gRPC), Dashboard to Operations API, Operations API to simulator admin. See CG-023 for the concrete consequence.

**Resolution:** Name the mechanism per hop (mTLS service identity for internal gRPC; session/JWT with role claims for the operator UI), and define the authorization matrix.

### CG-047 — MEDIUM — Operator vs. viewer permission model is listed as a topic but never specified

### CG-048 — MEDIUM — Audit log required content, storage, and retention are undefined
**File:** `docs/02-architecture/SECURITY_BOUNDARIES.md`

---

## H. Requirements, acceptance, testing, process

### CG-049 — HIGH — Most requirements have no acceptance criteria
**Files:** `docs/01-requirements/*`

10 AC items cover roughly 30 FRs and 12 NFRs. With no AC: FR-002, FR-012, FR-020, FR-021, FR-022, FR-030, FR-035, FR-052, and NFR-003, NFR-005 through NFR-010, NFR-012.

**Why it blocks:** The Definition of Done requires tests mapped to AC IDs; unmapped requirements cannot be closed.

### CG-050 — MEDIUM — AC-001 is weaker than the requirement it verifies
`FR-001` requires 20 equipment in demo profile; `AC-001` verifies "10+".

### CG-051 — MEDIUM — FR-021 is untestable as written
"either failure probability or RUL … should support both if feasible without delaying reliability work" — optional and unfalsifiable, while the prediction contract carries both fields.

### CG-052 — HIGH — No test specifications exist
`TEST_STRATEGY.md` is a 15-line pyramid. There are no per-AC test specifications, no gate-by-gate Safety Supervisor test table (which its own strategy demands), and no failure-test specifications beyond seven named scenarios.

### CG-053 — BLOCKER — Service designs are roughly 3 lines each against a 25-section requirement
**File:** `docs/02-architecture/SYSTEM_ARCHITECTURE.md`

Eight components are described in one short paragraph each. Implementation-ready design is absent for **all** of them. Four further required services (Kafka backbone, telemetry/operational data, observability platform, MLflow lifecycle) have no design document at all.

**Why it blocks:** This is the largest single gap between v0.2 and implementation-readiness.

### CG-054 — BLOCKER — Failure matrix is 10 rows by 4 columns against a 27 by 11 requirement
**File:** `docs/02-architecture/FAILURE_MODEL.md`

Missing rows include: Safety Supervisor unavailable, Control Service unavailable, Edge Gateway crash/restart, PostgreSQL unavailable, Operations API unavailable, consumer lag, duplicate event, out-of-order event, buffer overflow, network partition, clock skew, Kubernetes pod restart, observability unavailable, invalid model, low confidence, AI timeout (distinct from AI down), stale prediction (distinct from stale telemetry).
Missing columns include: control impact, data impact, operator visibility, alert, acceptable vs unacceptable consequence, and CONTROL-CRITICAL / CONTROL-INDEPENDENT classification.

### CG-055 — HIGH — No Mermaid diagrams exist
Only one ASCII block in MASTER_SPEC. Twelve diagrams are required and none can currently be cross-checked against contracts.

### CG-056 — HIGH — No Decision Record mechanism exists
`docs/09-decisions/` is absent; there is no record of *how* decisions were challenged, only ADRs stating outcomes.

### CG-057 — MEDIUM — Topic naming is inconsistent between plural and singular forms
`EVENT_CONTRACTS.md` uses `factory.predictions.v1` / `factory.safety-decisions.v1` (plural). Canonical names must be preserved or changed by explicit decision, not drift.

### CG-058 — LOW — Toolchain pins must be re-verified and digest-pinned at Phase 1 initialization
**File:** `docs/06-development/TOOLCHAIN.md`

### CG-059 — LOW — CHANGELOG has no `Unreleased` section, so in-flight documentation changes have nowhere to land

---

## Top 10 that would stop Phase 1

1. **CG-011** — "Control survives AI failure" is not guaranteed when the Supervisor itself dies.
2. **CG-019** — Kafka replay would re-issue real equipment commands.
3. **CG-004** — Command contract cannot detect stale or reordered intents.
4. **CG-012** — Equipment state machine does not exist but a safety gate depends on it.
5. **CG-033** — Quality flags are unconstrained strings, so the quality gate is undecidable.
6. **CG-014** — The two normative safety-gate lists disagree.
7. **CG-053** — Service designs are an order of magnitude short of implementation-ready.
8. **CG-023** — Command `source` is self-asserted, so control authority is not enforced.
9. **CG-001 / CG-002** — Contracts contradict their own examples, and 3 of 4 event types have no schema.
10. **CG-010 / CG-054** — The most safety-relevant failure mode has no defined behavior.
