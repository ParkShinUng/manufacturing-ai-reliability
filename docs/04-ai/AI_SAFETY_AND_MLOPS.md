# AI Safety and MLOps

> **This document is the single normative source for the safety gate list.** `MASTER_SPEC.md` §7
> previously carried a second, differently-ordered copy; that copy has been replaced by a reference
> to this table (DEC-005, GAP-025). Two normative copies of one list was the root cause of the
> conflict, so the duplicate was removed rather than re-synchronised.
> Closes: GAP-020, GAP-025, GAP-029, GAP-030, GAP-070, GAP-071, GAP-072, GAP-073, and the null
> aggregation requirement from DEC-008.

## 1. AI problem scope

Baseline uses an explainable, reproducible model stack rather than maximising model novelty:

- anomaly: Isolation Forest, autoencoder, or a robust statistical baseline;
- failure/RUL: LightGBM baseline before deep sequence models;
- a deep model only when it adds measurable value.

**Reason.** The portfolio evaluates production integration, failure handling, and lifecycle control.
A simple model with strong operational controls is worth more here than an opaque complex model with
weak operations.

---

## 2. Canonical safety gate table (normative)

Gates are evaluated **in this order**. Evaluation is **short-circuit for the decision** but
**exhaustive for the record**: the first failing gate determines the outcome, and every gate's result
is still recorded in `gateResults` so a rejection is diagnosable rather than merely asserted.

| # | Gate | Input | Pass condition | Reject reason code | Degraded behaviour |
|---|---|---|---|---|---|
| 1 | `SUPERVISOR_HEALTHY` | self health, config validity, lease validity | config valid, lease held and unexpired | `SUPERVISOR_UNHEALTHY` | no command issued; Control Service watchdog takes over after 12 s |
| 2 | `MODEL_AUTHORIZED` | model authorization record | model+version authorized, not quarantined, stage permitted for this equipment | `MODEL_QUARANTINED`, `MODEL_NOT_PRODUCTION`, `MODEL_STAGE_UNAUTHORIZED`, `MODEL_AUTHORIZATION_STALE` | fallback |
| 3 | `PREDICTION_FRESH` | `predictedAtUtc` vs **wall-clock now** | age ≤ 10 s + skew budget | `PREDICTION_STALE` | fallback |
| 4 | `PREDICTION_ORDERED` | `predictedAtUtc` vs last processed for this equipment | strictly newer | `PREDICTION_SUPERSEDED` | discard, keep current state |
| 5 | `TELEMETRY_FRESH` | newest `eventTimeUtc` vs wall-clock now | age ≤ 2 s + skew budget | `TELEMETRY_STALE` | fallback |
| 6 | `TELEMETRY_QUALITY` | `quality.overall` | `GOOD`, or `UNCERTAIN` with all safety-required channels clean | `SENSOR_QUALITY_BAD` | fallback |
| 7 | `FEATURE_COMPLETE` | `featureWindow.validSampleRatio` | ≥ 0.8 on every safety-required channel | `FEATURE_INCOMPLETE`, `FEATURE_WINDOW_INSUFFICIENT` | fallback |
| 8 | `OOD` | `outputs.oodScore` | **< authorized `oodThreshold`** | `OOD_HIGH` | **hard reject** → fallback |
| 9 | `CONFIDENCE` | `outputs.confidence` | ≥ authorized `confidenceThreshold` | `CONFIDENCE_LOW` | fallback |
| 10 | `EQUIPMENT_STATE_ELIGIBLE` | gateway-observed equipment state | state is **`RUNNING`** | `EQUIPMENT_STATE_INELIGIBLE` | fallback |
| 11 | `RATE_OF_CHANGE` | requested vs baseline | delta ≤ 10 pp | *(clamped, not rejected)* | accept with `RATE_CHANGE_CLAMPED` |
| 12 | `RATE_BUDGET` | rolling 60 s cumulative movement | ≤ 25 pp | `RATE_BUDGET_EXHAUSTED` | fallback |
| 13 | `OPERATING_BOUNDS` | final target | within 60–100 % | `OPERATING_BOUND_VIOLATION` | fallback |

All 13 gates pass → `ACCEPT` with `AI_ACCEPTED`.

### 2.1 OOD is a hard reject (DEC-005)

v0.2's `FAILURE_MODEL.md` allowed OOD to "reject **or reduce AI authority** per model policy," which
contradicted `MASTER_SPEC.md`'s "any failed gate rejects." **OOD is now a hard reject.** A
reduced-authority path would be a second, partially-trusted acceptance mode: it roughly doubles the
safety state space, needs its own thresholds and its own test matrix, and is exactly the nuance that
cannot be exhaustively tested at portfolio scale. Graded authority is recorded as **deliberately
deferred**, available later by ADR with measured evidence.

### 2.2 Gate 11 clamps; it does not reject (GAP-033)

v0.2's `RATE_CHANGE_LIMITED` was ambiguous — the name implied clamping, its placement in the
rejection list implied rejection. It is **removed** and split:

- **`RATE_CHANGE_CLAMPED`** — a non-rejecting modifier on an `ACCEPT`. The decision record stores
  both `requestedOperationRatePct` and `boundedOperationRatePct`, so clamping is auditable as a
  modification of AI intent rather than silently swallowed.
- **`RATE_BUDGET_EXHAUSTED`** — genuinely rejecting (gate 12).

### 2.3 The rate-of-change baseline is the last **commanded** value (GAP-034)

Baseline is the **last accepted commanded setpoint**, never the last applied
`operationRatePct`. The drive slews at 15 %/s (`EQUIPMENT_MODEL_AND_STATE.md` §1.4), so a mid-slew
reading sits between the old and new values; using it would make the next delta appear smaller than
it really is and let the budget be spent twice.

Tracking failure is caught separately by equipment transition T7 (`RUNNING → DEGRADED` when applied
deviates from commanded by more than 10 pp for over 5 s), which is the right place for it.

**Cumulative budget: 25 pp of net movement per 60 s rolling window per equipment.** The per-decision
limit alone would permit 100 → 60 % in roughly 20 s at a 5 s cadence; the window budget stretches the
full authorized range to at least ~96 s.

---

## 3. Fallback policy and its configuration contract (GAP-029)

Fallback was described in v0.2 as "deterministic and configuration-backed" with no schema, owner,
reload behaviour, or invalid-config behaviour — so the deterministic path was not, in fact,
deterministically implementable.

| Aspect | Rule |
|---|---|
| Owner | **Control Service** (it must be able to act when the Supervisor is gone) |
| Storage | version-controlled file, mounted read-only; `SAFETY_CONFIGURATION.md` holds the schema |
| Validation | **at startup, fail-closed**: an invalid or unparseable config aborts startup; the service does **not** start with defaults |
| Reload | explicit operator-triggered reload only; never a filesystem watcher |
| Reload validation | validated before swap; on failure the previous valid config is retained and an alert is raised |
| Missing equipment entry | treated as invalid config → startup abort; never an implicit default |

Fail-closed at startup rather than defaulting is deliberate: a service that silently starts with a
guessed fallback rate is more dangerous than one that refuses to start, because the guess is
invisible.

Baseline values (simulator profile): fallback rate 60 %, AI-authorized range 60–100 %, per-decision
delta 10 pp, window budget 25 pp / 60 s, telemetry freshness 2 s, prediction TTL 10 s.

`STOP_REQUIRED` entry conditions are defined in `CONTROL_MODE_STATE_MACHINE.md` §5 — v0.2's
discretionary "may escalate" has been replaced by deterministic triggers.

---

## 4. Null handling in feature aggregation (DEC-008)

A 60 s window containing nulls must aggregate **identically across a feature-builder restart**, or
training-serving skew appears from nothing.

| Rule | |
|---|---|
| Nulls are **excluded** from aggregates | never zero-filled — zero is a physically meaningful reading and would corrupt mean/std |
| Every aggregate carries `validSampleRatio` | valid samples ÷ **expected** samples |
| Expected count derives from window duration × cadence | **not** from records received, so a restart mid-window cannot change the denominator |
| Window boundaries are absolute event-time boundaries | aligned to wall-clock second boundaries, so they are restart-invariant |
| `validSampleRatio < 0.8` on any safety-required channel | **no prediction is produced at all** |
| `validSampleRatio < 0.8` on an advisory channel only | prediction proceeds; the ratio is carried on the event |

**No prediction beats a low-confidence prediction.** A prediction from a half-empty window would be
assigned a confidence by a model never trained on half-empty windows — the confidence itself would be
out of distribution. Suppressing it yields a clean `AI_UNAVAILABLE` fallback instead of a confidently
wrong recommendation.

---

## 5. Model authorization as an event stream (DEC-009)

v0.2 had the Supervisor consult MLflow, which created a synchronous safety dependency that
`SYSTEM_ARCHITECTURE.md` forbids — while a cache with a staleness bound left a window in which a
quarantined model still held authority.

**Resolution: publish authorization, do not poll it.**

- `mlops-publisher` translates MLflow registry transitions (promote, quarantine, archive) into
  `ModelAuthorization` events on the compacted, infinite-retention topic
  `factory.model-deployments.v1`.
- The Supervisor consumes that topic and holds the compacted state in memory. **It never calls MLflow
  on the decision path.**
- Quarantine propagates in milliseconds rather than a polling interval.
- An MLflow outage has **zero** effect on the Supervisor: authorization already lives in Kafka.

### 5.1 Publisher liveness — closing the silence hole

Codex identified the remaining fail-open in round 2: if `mlops-publisher` is down when a quarantine
is issued, Kafka stays reachable and simply **quiet**, so a staleness bound on the Kafka connection
never trips. Silence is otherwise indistinguishable from "nothing changed."

`mlops-publisher` therefore emits an **`AuthorizationWatermark`** event every **30 s** even when
nothing has changed. The Supervisor tracks time since the last watermark:

| Condition | Behaviour |
|---|---|
| watermark age ≤ 90 s | authorization state trusted |
| watermark age > 90 s (3 missed) | **all models treated as unauthorized** → `MODEL_AUTHORIZATION_STALE` → fallback |
| no watermark ever seen at startup | models unauthorized until the first watermark arrives |

A quarantine write must be durably produced with `acks=all` **before** the MLflow registry transition
is reported complete, so the authoritative record cannot lag the registry.

This makes publisher death **detectable** rather than silent, which was the actual defect.

### 5.2 `ModelAuthorization` record

`modelName, modelVersion, modelRunId, deploymentStage, oodThreshold, confidenceThreshold,
featureSchemaVersion, quarantined, authorizedEquipmentCohort[], authorizationSequence, issuedAtUtc,
validUntilUtc`

Predictions carry `deploymentStage` and `featureSchemaVersion` (GAP-070, GAP-073) and are accepted
only when both match an authorization record whose stage is permitted for that equipment.

---

## 6. MLflow lifecycle

`Experiment → Registered Model → Candidate → Shadow → Canary → Production → Archived/Quarantined`

Every Production model records: git commit, training run ID, dataset ID/version, metrics, feature
schema version, OOD/confidence thresholds, supported equipment profile.

## 7. Promotion gates (GAP-072)

No model is promoted because aggregate accuracy improved. Quantitative gates, all required:

| Gate | Threshold | Rationale |
|---|---|---|
| Anomaly detection recall on labelled degradation | ≥ 0.85 | missing degradation is the costly error |
| False-positive rate on `NORMAL` profile | ≤ 0.05 | false alarms destroy operator trust |
| RUL MAE on held-out degradation runs | ≤ 15 % of `T_fail` | |
| OOD detection rate on `OOD_PROFILE` | ≥ 0.90 | the OOD gate must actually fire |
| Inference latency P95 | ≤ **500 ms** | must fit inside the 5 s cadence with margin |
| Shadow agreement with Production | Cohen's κ ≥ 0.6 over ≥ 24 h | |
| Safety-gate rejection rate in shadow replay | within ±10 pp of Production | a candidate that is accepted far more often is suspicious, not better |
| Approval owner | **human**, recorded in the model registry | |

**Rollback trigger** (any, evaluated over a 15-minute window during canary):
fallback rate rises > 20 pp above the Production baseline; rejection rate > 50 %; inference error
rate > 1 %; P95 latency > 500 ms. Rollback is: publish `quarantined=true` for the candidate, which
propagates through `factory.model-deployments.v1` in milliseconds, and the Supervisor stops accepting
its predictions at the next decision.

**Model quality is not system safety.** A high model metric never waives a safety-gate test.
