# Reconciled Gap Analysis — Specification v0.2 → v0.3

> Sources: `reviews/v0.3/CLAUDE_GAP_ANALYSIS.md` (59 findings, IDs `CG-nnn`)
> and `reviews/v0.3/CODEX_GAP_ANALYSIS_raw.md` (30 findings, IDs `CODEX-GAP-nnn`).
> Both analyses were produced independently; neither agent saw the other's output before writing.
> This document is the **authoritative merged backlog** for v0.3. Unified IDs are `GAP-nnn`.

## 1. Independence check — what the dual-agent pass actually bought

| Category | Count | Meaning |
|---|---|---|
| **Convergent** (both agents found it) | 19 | High confidence these are real. |
| **Claude-only** | 31 | Codex missed. |
| **Codex-only** | 11 | Claude missed. |
| **Unified total after merge** | **62** | |

The 11 Codex-only findings are the justification for the dual-agent protocol. Four of them are
material to Phase 1 and were genuine blind spots in the Claude pass:

- **GAP-045 (OPC UA / Modbus address maps)** — Phase 1 and Phase 2 deliver protocol endpoints and a
  normalizing gateway. Without node/register maps, scaling, endianness, and data-type mapping, that
  work is unbuildable. Claude did not detect this at all.
- **GAP-044 (simulator dynamics)** — Phase 1's headline deliverable is the simulator. Sensor ranges,
  units, degradation equations, and fault dynamics are entirely unspecified.
- **GAP-026 (manual-mode command origin)** — `API_AND_COMMAND_CONTRACTS.md` permits
  "explicit human/manual mode" to originate production commands. This is a **second command origin
  with no state, no authorization, no interlock, and no relationship to the Safety Supervisor**.
  It is a latent bypass of the entire deterministic safety path, sitting in plain sight in a
  contract document.
- **GAP-020 (OOD contradiction)** — `MASTER_SPEC.md` §7 states any failed gate rejects the
  recommendation; `FAILURE_MODEL.md` states OOD may "reject **or reduce AI authority** per model
  policy." These are different safety semantics on the same path.

Conversely, the single highest-severity finding in the whole review (**GAP-021**, Supervisor death
leaves no fallback commander) was found only by Claude. Neither agent alone was sufficient.

---

## 2. Unified findings

Severity: **B** = BLOCKER, **H** = HIGH, **M** = MEDIUM, **L** = LOW.
Origin: **C** = Claude only, **X** = Codex only, **C+X** = convergent.

### A. Contracts and schemas

| ID | Sev | Origin | Finding | Source IDs |
|---|---|---|---|---|
| GAP-001 | B | C+X | Telemetry doc example omits schema-required `voltageV`, `torqueNm`, `operationRatePct`; the example fails its own schema. | CG-001, CODEX-GAP-002 |
| GAP-002 | B | C+X | Only telemetry has a machine-readable schema. Prediction, safety-decision, command-outcome, equipment-state, model-deployment, fault-injection have none. | CG-002, CODEX-GAP-005 |
| GAP-003 | B | C | No common event envelope; `eventId`/`schemaVersion`/`sequence`/`ingestTime` present on telemetry only. No `eventType`, no `causationId`. | CG-003 |
| GAP-004 | B | C | Command contract cannot detect a stale or reordered intent: no `issued_at`, no monotonic intent sequence, no decision linkage. Older intent can win. | CG-004 |
| GAP-005 | H | C+X | OpenAPI is a stub: no schemas, error model, pagination, filters, or auth. | CG-005, CODEX-GAP-006 |
| GAP-006 | H | C+X | Proto uses bare `string` for timestamps, `source`, `unit`, `reason_code`; no enums, no validation contract, no invalid-field behavior. | CG-006, CODEX-GAP-007 |
| GAP-007 | H | C | No DLQ / poison-message policy, though ADR-0001 names the obligation. | CG-007 |
| GAP-008 | H | C | Command-outcome and equipment-state Kafka topics are required by ADR-0009 and SYSTEM_ARCHITECTURE §7 but defined nowhere. | CG-008 |
| GAP-009 | M | C | No runtime schema-validation or registry decision; `schemaVersion` mismatch behavior undefined. | CG-009 |
| GAP-010 | H | X | Contract precedence is ambiguous: `REPOSITORY_STRUCTURE.md` calls `contracts/` the machine-readable source of truth, while `MASTER_SPEC.md` §9 ranks `docs/03-contracts` above it. | CODEX-GAP-030 |
| GAP-011 | M | C | Topic naming drifts between plural (`factory.predictions.v1`) and singular forms. | CG-057 |

### B. Safety, control authority, state machines

| ID | Sev | Origin | Finding | Source IDs |
|---|---|---|---|---|
| GAP-020 | B | X | `MASTER_SPEC.md` ("any failed gate rejects") contradicts `FAILURE_MODEL.md` ("OOD may reject **or reduce AI authority**"). Two different safety semantics. | CODEX-GAP-015 |
| GAP-021 | B | C | **"Control survives AI failure" holds for inference death but not Supervisor death.** The Supervisor is the only command origin; if it dies, nothing drives equipment to fallback and it holds a possibly AI-elevated rate indefinitely. | CG-011 |
| GAP-022 | B | C | `FAILURE_MODEL.md` has no row for Safety Supervisor unavailable; gate 9 ("Supervisor is healthy") is self-evaluated and therefore vacuous when it is down. | CG-010 |
| GAP-023 | B | C+X | Equipment state machine does not exist, yet gate 7 and reason code `EQUIPMENT_STATE_INELIGIBLE` depend on it. | CG-012, CODEX-GAP-019 |
| GAP-024 | B | C+X | Control mode state machine (`NORMAL_RULE`/`AI_ASSISTED`/`SAFE_FALLBACK`/`STOP_REQUIRED`) has no owner, transitions, guards, precedence, or race handling. | CG-013, CODEX-GAP-008 |
| GAP-025 | B | C | The two normative gate lists disagree (MASTER_SPEC §7 = 9 gates; AI_SAFETY_AND_MLOPS = 10 gates, different order). REASON_CODES implements the union, matching neither. | CG-014 |
| GAP-026 | B | X | **"explicit human/manual mode" is a second production command origin with no state, authorization, interlock, command limits, or Supervisor relationship.** Latent bypass of the deterministic safety path. | CODEX-GAP-010 |
| GAP-027 | B | C | Kafka replay of predictions re-issues real commands: replay produces a new `decisionId`, hence a new idempotency key, so the command applies again. AC-003's claim is not delivered by the design. | CG-019 |
| GAP-028 | B | C | Command `source` is a self-asserted string; any process reaching the gRPC port can claim to be the Supervisor. Defeats ADR-0002. | CG-023 |
| GAP-029 | B | X | Fallback is "configuration-backed per equipment class" with no config schema, ownership, reload behavior, or invalid-config behavior. The deterministic path is not deterministically implementable. | CODEX-GAP-009 |
| GAP-030 | B | X | OOD threshold, confidence threshold, feature completeness, and equipment eligibility are named but never numerically or logically defined. | CODEX-GAP-016 |
| GAP-031 | H | X | Safety Supervisor "consumes predictions plus required equipment state", but no equipment-state contract, source, or freshness rule exists. | CODEX-GAP-017 |
| GAP-032 | H | C+X | No rules for stale/duplicate/reordered/superseded predictions; 2-3 predictions are simultaneously within TTL (5 s cadence, 10 s TTL). | CG-018, CODEX-GAP-018 |
| GAP-033 | H | C | `RATE_CHANGE_LIMITED` is ambiguous: name implies clamp, placement implies reject, contract implies clamp-then-accept. | CG-015 |
| GAP-034 | H | C | Rate-of-change limit has no cumulative budget (100%→60% in ~20 s) and no defined baseline (last commanded vs last confirmed applied). | CG-016 |
| GAP-035 | H | C | Escalation to `STOP_REQUIRED` is discretionary ("may"), with no trigger, owner, acknowledgement, or exit path. | CG-017 |
| GAP-036 | H | C | Idempotency store has no defined storage, TTL, or restart behavior. | CG-021 |
| GAP-037 | H | C | Idempotency protects only transport retry of a single decision; that scope limit is undocumented and AC-003 implies more. | CG-020 |
| GAP-038 | H | C | Control Service behavior on protocol write failure is undefined; OT-level idempotency of the setpoint write is never stated. | CG-022 |
| GAP-039 | M | C | Fault injection writes to the simulator without passing Control Service, contradicting "only Control Service may perform equipment writes". Needs an explicit simulator-admin carve-out. | CG-024 |

### C. OT / simulator / protocol

| ID | Sev | Origin | Finding | Source IDs |
|---|---|---|---|---|
| GAP-044 | B | X | **Simulator physics undefined**: no sensor ranges, units, nominal values, degradation equations, fault dynamics, or stop conditions. Phase 1's primary deliverable would be invented in code. | CODEX-GAP-019 |
| GAP-045 | B | X | **No OPC UA node map and no Modbus register map**: no address space, data types, scaling, endianness, quality mapping, or write-address map. | CODEX-GAP-020 |

### D. Distributed systems

| ID | Sev | Origin | Finding | Source IDs |
|---|---|---|---|---|
| GAP-050 | H | C+X | Offset commit policy, replay window, duplicate identity, and consumer restart semantics are undefined per topic. | CG-019/020, CODEX-GAP-011 |
| GAP-051 | H | C+X | `sequence` scope, monotonicity, reset, and gap handling undefined. | CG-027, CODEX-GAP-012 |
| GAP-052 | H | C+X | Gateway buffer capacity, overflow/drop policy, and Kafka-down behavior undefined. | CG-028, CODEX-GAP-013 |
| GAP-053 | H | X | No numeric timeout, retry count, backoff, jitter, or circuit-breaker profile for any dependency. | CODEX-GAP-014 |
| GAP-054 | H | C | Per-equipment ordering asserted without its preconditions (immutable partition count, idempotent producer, bounded in-flight requests). | CG-026 |
| GAP-055 | H | C | No partition count, replication factor, `min.insync.replicas`, or retention for any topic. | CG-025 |
| GAP-056 | M | C | Consumer group naming convention undefined. | CG-029 |
| GAP-057 | M | C | Consumer lag has no threshold or alert rule. | CG-030 |

### E. Time and data quality

| ID | Sev | Origin | Finding | Source IDs |
|---|---|---|---|---|
| GAP-060 | B | C+X | No clock model: no clock source per field, no skew budget, no NTP precondition, no event-time vs ingest-time rule per gate/aggregation, no monotonic-clock guidance. | CG-031, CODEX-GAP-021 |
| GAP-061 | B | C+X | `quality.flags` is an unconstrained string array, so the `SENSOR_QUALITY_BAD` gate has no decidable input domain and metric cardinality is unbounded. | CG-033, CODEX-GAP-003 |
| GAP-062 | H | C | **A dead sensor has no legal representation**: all 7 measurements are `required`, `additionalProperties:false`, and JSON has no NaN. The producer must fabricate a value or emit an invalid event. | CG-034 |
| GAP-063 | H | C | Timestamp reversal (`eventTimeUtc` after `ingestTimeUtc`) is permitted by the schema with no flag or behavior. | CG-032 |
| GAP-064 | H | C | "Required sensors" per gate and per model is never mapped, so `FEATURE_INCOMPLETE` is undecidable. | CG-035 |

### F. MLOps

| ID | Sev | Origin | Finding | Source IDs |
|---|---|---|---|---|
| GAP-070 | B | C+X | `deploymentStage` is mandated by ADR-0010 but absent from the prediction contract; shadow/canary output is indistinguishable from production at the decision point. | CG-036, CODEX-GAP-004 |
| GAP-071 | H | C+X | Model metadata cache contents, validation, expiry, startup policy, and quarantine propagation are undefined; quarantine during an MLflow outage fails **open**. | CG-038, CODEX-GAP-022 |
| GAP-072 | H | C+X | Promotion checks are qualitative: no metric thresholds, approval owner, rollback trigger, or auditable model-deployment contract. | CG-040/041, CODEX-GAP-023 |
| GAP-073 | H | C | Feature schema version is not carried on predictions, so training-serving skew is undetectable at decision time. | CG-037 |
| GAP-074 | M | C | Canary cohort storage and change control undefined. | CG-039 |

### G. Persistence, observability, security

| ID | Sev | Origin | Finding | Source IDs |
|---|---|---|---|---|
| GAP-080 | H | X | PostgreSQL table/read-model schemas, retention, migration ownership, and rebuild-from-Kafka behavior are undefined. | CODEX-GAP-024 |
| GAP-081 | H | C+X | No authn/authz mechanism is named for any hop (Supervisor→Control, Dashboard→Operations API, Operations API→simulator admin); no credential rotation; no demo-endpoint guard. | CG-046, CODEX-GAP-025 |
| GAP-082 | H | C+X | Metrics lack types, units, label sets, ownership, alert thresholds, and SLO measurement windows. | CG-043/044, CODEX-GAP-026 |
| GAP-083 | H | C | NFR document has no measurable targets; the SLO document states different ones with no cross-reference. | CG-042 |
| GAP-084 | M | C | Operator vs viewer permission model unspecified. | CG-047 |
| GAP-085 | M | C | Audit log content, storage, and retention undefined. | CG-048 |
| GAP-086 | L | C | `model_version_info` labelled gauge is a cardinality growth risk. | CG-045 |

### H. Requirements, testing, process

| ID | Sev | Origin | Finding | Source IDs |
|---|---|---|---|---|
| GAP-090 | B | C | Service designs are ~3 lines each against a ~25-section requirement; 4 required services have no design document at all. | CG-053 |
| GAP-091 | B | C | Failure matrix is 10×4 against a ~27×11 requirement and lacks CONTROL-CRITICAL classification. | CG-054 |
| GAP-092 | H | C+X | Most P0 requirements have no acceptance criteria (FR-002/003/005/010/012/020/021/022/030/035/052; NFR-003/005-010/012). | CG-049, CODEX-GAP-027 |
| GAP-093 | M | C+X | AC-001 ("10+") is weaker than FR-001 ("20"); a system can pass acceptance while failing the P0 requirement. | CG-050, CODEX-GAP-028 |
| GAP-094 | H | C+X | Failure tests lack setup, trigger, expected transitions, thresholds, and pass/fail assertions. | CG-052, CODEX-GAP-029 |
| GAP-095 | H | C | No Mermaid diagrams exist; 12 are required. | CG-055 |
| GAP-096 | H | C | No Decision Record mechanism exists. | CG-056 |
| GAP-097 | B | X | `MASTER_SPEC.md` §10 and `IMPLEMENTATION_PLAN.md` still declare the documentation gate closed. v0.3 must explicitly state gate status. | CODEX-GAP-001 |
| GAP-098 | M | C | FR-021 is untestable as written ("either … should support both if feasible"). | CG-051 |
| GAP-099 | L | C | Toolchain pins need a re-verification and digest-pinning procedure at Phase 1 init. | CG-058 |
| GAP-100 | L | C | CHANGELOG has no `Unreleased` section. | CG-059 |

---

## 3. Severity totals (unified)

| Severity | Count |
|---|---|
| BLOCKER | 22 |
| HIGH | 27 |
| MEDIUM | 10 |
| LOW | 3 |
| **Total** | **62** |

## 4. Architecture-critical decisions extracted from this backlog

The following gaps cannot be closed by documentation alone — they require an architectural
**decision**, and therefore the Claude↔Codex decision protocol plus an ADR:

| Decision | Driving gaps |
|---|---|
| **DEC-001** Who guarantees fallback when the Safety Supervisor dies? | GAP-021, GAP-022 |
| **DEC-002** Is the Safety Supervisor replay-eligible, and how are replayed predictions prevented from moving equipment? | GAP-027, GAP-050 |
| **DEC-003** How is command ordering/supersession enforced end to end? | GAP-004, GAP-032 |
| **DEC-004** Does manual mode exist, and if so under what authority and interlock? | GAP-026 |
| **DEC-005** Is OOD a hard reject or a reduced-authority mode? | GAP-020, GAP-025 |
| **DEC-006** How is command-origin authenticity enforced? | GAP-028, GAP-081 |
| **DEC-007** What is the canonical contract precedence and event envelope? | GAP-003, GAP-010 |
| **DEC-008** How is a missing/failed sensor represented without lying to the gates? | GAP-061, GAP-062 |
| **DEC-009** How does model authorization (stage/quarantine) stay fail-closed during a registry outage? | GAP-070, GAP-071 |

These nine are carried into `docs/09-decisions/` as Decision Records.
