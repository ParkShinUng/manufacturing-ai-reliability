# Claude Round-1 Proposals — 9 architecture-critical decisions

> Input to the Claude↔Codex decision protocol. Concise engineering rationale only.
> Each proposal states the decision, the reasoning, the alternatives considered, and the
> open parameter values marked `TBD-WITH-DECISION`.

---

## DEC-001 — Who guarantees fallback when the Safety Supervisor dies?
**Drives:** GAP-021, GAP-022

**Problem.** The Safety Supervisor is the only component that issues commands. `SAFE_FALLBACK` is a
*mode of the Supervisor*, not a property of the system. Kill the Supervisor and nothing drives the
equipment anywhere: it holds its last commanded rate, which may be an AI-elevated rate. MASTER_SPEC
principle 3 ("Control survives AI failure") is therefore only true for inference death.

**Proposal.** Two independent layers, both deterministic, neither containing AI logic:

1. **Control Service fallback watchdog (application layer).** Control Service tracks, per equipment,
   time since the last Supervisor liveness signal (accepted command or explicit heartbeat RPC). If
   silence exceeds `supervisor_silence_timeout_ms` (`TBD-WITH-DECISION`, proposed 15 000 ms = 3×
   inference cadence), Control Service autonomously writes the configured fallback rate, sets mode
   `SAFE_FALLBACK`, and publishes a control-outcome event with reason `SUPERVISOR_SILENT`.
   Control Service still owns every equipment write, so ADR-0002's single-writer property is intact.
2. **Simulator/equipment dead-man timer (OT layer).** The simulated drive requires a periodic
   setpoint refresh. Absent a refresh within `equipment_deadman_timeout_ms`
   (`TBD-WITH-DECISION`, proposed 30 000 ms), it self-reverts to its safe default rate.

**Why both.** Layer 2 is what a real plant does (the watchdog lives in the PLC, not in the
application). It costs nothing here because we own the simulator, and it is the only layer that
survives Control Service death too. Layer 1 gives fast, observable, auditable reaction.

**Alternatives considered.**
- *A: Control Service watchdog only.* Rejected as sole measure: does not survive Control Service death.
- *B: Simulator dead-man only.* Slower, and produces no application-level audit trail.
- *C: Do nothing; document that equipment holds last rate.* Honest but abandons the headline claim.

**Recommendation.** Both layers.

---

## DEC-002 — Is the Safety Supervisor replay-eligible?
**Drives:** GAP-027, GAP-050

**Problem.** The Supervisor consumes predictions from Kafka. On replay it re-evaluates historical
predictions, mints a **new** `decisionId`, and therefore a **new** idempotency key — so the command
is applied again. AC-003 claims replay produces no duplicate command effect; the design does not
deliver that.

**Proposal.** The Supervisor consumer group is **not replay-eligible**, enforced by three
independent mechanisms so that no single mistake re-animates old predictions:

1. **Offset policy.** Supervisor consumer groups use `auto.offset.reset=latest` and are never reset
   to `earliest` by any documented operational procedure. Replay is confined to projection and
   analytics groups (Operations Service, training feature builds).
2. **TTL evaluated against wall clock.** The prediction TTL gate compares `predictedAtUtc` against
   **now**, never against the consuming event's own timestamp. A replayed prediction is therefore
   already expired by construction and rejected with `PREDICTION_STALE`. This mechanism is free — the
   gate already exists — but only if the comparison basis is stated normatively, which it currently
   is not.
3. **Control Service independent check.** Commands carry `source_prediction_at_utc`; Control Service
   rejects any command whose source prediction is older than the TTL bound
   (`COMMAND_SOURCE_STALE`), regardless of what the Supervisor believed.

**Why.** Mechanism 2 alone makes replay harmless, and it needs no new component. Mechanisms 1 and 3
are cheap defence in depth against an operator resetting offsets or a Supervisor bug.

**Alternatives considered.**
- *A: Make decisions idempotent by deriving `decisionId` deterministically from `predictionId`.*
  Attractive, but a re-decision on the same prediction is legitimately *different* if equipment
  state changed, so a stable ID would wrongly suppress a real new decision.
- *B: Forbid replay entirely.* Destroys the `FAIL-KAFKA-001` portfolio demonstration.

**Recommendation.** All three mechanisms; state mechanism 2 normatively in MASTER_SPEC.

---

## DEC-003 — How is command ordering / supersession enforced?
**Drives:** GAP-004, GAP-032

**Problem.** Ordering is currently decided by arrival. Two unexpired commands arriving out of order
apply in arrival order, so an *older* intent can win.

**Proposal.** Extend the command contract with `decision_id`, `issued_at_utc`, `intent_sequence`
(per-equipment strictly monotonic `uint64`, assigned by the Supervisor), and
`source_prediction_at_utc`. Control Service persists `last_applied_intent_sequence` per equipment and
rejects any command with `intent_sequence <= last_applied_intent_sequence` using
`COMMAND_SUPERSEDED`. On the Supervisor side, predictions are processed in per-equipment monotonic
`predictedAtUtc` order; an older prediction arriving after a newer one is dropped with
`PREDICTION_SUPERSEDED`.

**Why.** Converts last-writer-wins-by-arrival into last-writer-wins-by-intent, which is the only
correct semantic for a setpoint. Sequence beats timestamp as the primary key because it is immune to
clock skew (DEC/GAP-060).

**Recommendation.** Adopt as stated.

---

## DEC-004 — Does manual mode exist, and under what authority?
**Drives:** GAP-026

**Problem.** `API_AND_COMMAND_CONTRACTS.md` permits "explicit human/manual mode" to originate
production commands. Nothing defines that mode: no state, no authorization, no interlock, no
relationship to the Supervisor. As written it is a documented bypass of the entire safety path.

**Proposal.** Manual mode exists, but as a **first-class controlled state**, not an escape hatch.
Introduce `MANUAL_OPERATOR` as a fifth control mode with these invariants:

- entering it requires an authenticated operator holding the `control:write` role;
- while active, AI authority is **fully suspended** — the Supervisor issues no AI-derived commands
  and emits decisions carrying `MANUAL_MODE_ACTIVE`;
- operator commands still flow **through Control Service** and are still subject to absolute
  operating bounds, rate-of-change limits, idempotency, and audit;
- `source=OPERATOR` is derived from verified identity, never from the payload (see DEC-006);
- manual mode **cannot** override `STOP_REQUIRED`;
- exit requires an explicit operator action and is audit-logged;
- a dead-man applies: operator inactivity beyond `manual_mode_idle_timeout_ms`
  (`TBD-WITH-DECISION`, proposed 300 000 ms) reverts to `SAFE_FALLBACK`.

**Scope.** v0.3 defines the mode, its contract, and its interlocks. **Implementation is P1**, after
the AI path. The v0.2 sentence that created the hole is rewritten to reference this definition.

**Alternatives considered.**
- *A: Delete manual mode from the baseline.* Simpler and defensible, but leaves a real operational
  need unaddressed and would make the existing contract sentence a dangling reference.

**Recommendation.** Define now, implement at P1.

---

## DEC-005 — Is OOD a hard reject or a reduced-authority mode?
**Drives:** GAP-020, GAP-025

**Problem.** `MASTER_SPEC.md` §7: any failed gate rejects. `FAILURE_MODEL.md`: OOD may "reject **or
reduce AI authority** per model policy." Two different safety semantics on the same path.

**Proposal.** **Hard reject** in the v0.3 baseline. Delete "or reduce AI authority" from
`FAILURE_MODEL.md`.

**Why.** A reduced-authority path is a second, partially-trusted acceptance mode. It roughly doubles
the safety state space, requires its own threshold set and its own test matrix, and is exactly the
kind of nuance that cannot be exhaustively tested at portfolio scale. MASTER_SPEC is also the higher
authority document. Graded authority can be introduced later by ADR with measured evidence.

**Recommendation.** Hard reject; record graded authority as a deliberately deferred option.

---

## DEC-006 — How is command-origin authenticity enforced?
**Drives:** GAP-028, GAP-081

**Problem.** `source` is a string the caller sets. Any process that can reach the Control Service
gRPC port can claim to be the Supervisor. This defeats ADR-0002 entirely.

**Proposal.** mTLS between Supervisor and Control Service with workload identities.
`source` is **removed as a request input** and becomes a server-derived value taken from the verified
peer certificate identity; it is echoed in the response for audit only. Control Service holds an
authorization map (identity → permitted command types → permitted equipment scope) and rejects
unknown identities with `COMMAND_SOURCE_UNAUTHORIZED`.

For the local/demo profile only, mTLS may be replaced by a shared-secret header when
`PROFILE=local`; this substitution must be logged as a warning at startup and is forbidden in the
production-like profile.

**Recommendation.** Adopt as stated.

---

## DEC-007 — Canonical contract precedence and event envelope
**Drives:** GAP-003, GAP-010

**Problem.** `REPOSITORY_STRUCTURE.md` declares `contracts/` the machine-readable source of truth;
`MASTER_SPEC.md` §9 ranks `docs/03-contracts` above it. When prose and schema disagree — and they
already do (GAP-001) — precedence is ambiguous.

**Proposal.**
1. **Machine-readable contracts in `contracts/` are authoritative.** `docs/03-contracts/*` becomes
   explanatory, and every example it contains must be a validated instance of the corresponding
   schema, enforced by a contract test. MASTER_SPEC §9 is amended accordingly.
2. **Common event envelope** for every Kafka event:
   `eventId, eventType, schemaVersion, equipmentId, occurredAtUtc, ingestTimeUtc, sequence,
   correlationId, causationId, producer, payload`.

**Recommendation.** Adopt as stated.

---

## DEC-008 — How is a missing or failed sensor represented?
**Drives:** GAP-061, GAP-062

**Problem.** All seven measurements are `required` with `additionalProperties:false`, and JSON has no
NaN. A dead sensor cannot be encoded at all, so a producer must either fabricate a value — feeding
the safety gates a lie — or emit an invalid event.

**Proposal.**
- Measurement values become `["number","null"]`. `null` means "no trustworthy value exists".
- Every `null` **must** be accompanied by a quality flag identifying the sensor and cause.
- **Substituting a synthetic or last-known value is forbidden.** Interpolation may occur only in
  explicitly-labelled derived feature aggregates, never in the raw telemetry contract.
- `quality.flags` becomes a **closed enum**: `SENSOR_MISSING, SENSOR_DISCONNECTED, SENSOR_FROZEN,
  VALUE_OUT_OF_RANGE, VALUE_NOT_FINITE, STALE_READING, OUTLIER_SUSPECTED, TIMESTAMP_REVERSED,
  DUPLICATE_SUSPECTED, SEQUENCE_GAP, BUFFER_OVERFLOW_DROP`.
- `quality.overall` is **derived, not free**: `BAD` if any safety-required sensor is null or carries
  a disqualifying flag; `UNCERTAIN` if only non-required sensors are affected; else `GOOD`.

**Recommendation.** Adopt as stated.

---

## DEC-009 — How does model authorization stay fail-closed during a registry outage?
**Drives:** GAP-070, GAP-071

**Problem.** ADR-0008 requires graceful degradation when MLflow is unavailable. But if a model is
quarantined **during** an outage, a degraded Supervisor keeps trusting it. That is fail-open in the
one direction that matters.

**Proposal.** The Supervisor holds a cached `ModelAuthorization` record:
`modelName, modelVersion, deploymentStage, oodThreshold, confidenceThreshold, featureSchemaVersion,
quarantined, authorizedEquipmentCohort, issuedAtUtc, validUntilUtc`.

- Predictions carry `deploymentStage` and `modelRunId` (GAP-070) and are accepted only if they match
  an authorization record whose stage is authorized for that equipment.
- The cache has a hard `model_authorization_max_staleness_ms` (`TBD-WITH-DECISION`, proposed
  300 000 ms). If MLflow is unreachable **and** the cache is older than that bound, the model is
  treated as **not authorized** → `MODEL_AUTHORIZATION_STALE` → fallback.

**Why.** This satisfies ADR-0008's "degrade gracefully" — a short registry blip changes nothing —
while making the safety-relevant direction fail **closed** rather than open.

**Recommendation.** Adopt as stated.
