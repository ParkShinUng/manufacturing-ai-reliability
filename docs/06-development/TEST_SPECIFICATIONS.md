# Test Specifications

> Closes GAP-094 (failure tests lacked setup, trigger, expected transitions, thresholds, and
> pass/fail assertions) and GAP-052/GAP-092 (no per-AC test specifications existed).
> Strategy and pyramid: `TEST_STRATEGY.md`. This document is the executable-detail layer.

## 1. Suites

| Suite | Location | Runs |
|---|---|---|
| Unit | alongside source, e.g. `src/dotnet/EquipmentSimulator.Tests/` | every commit |
| **Contract** | `tests/contract/` | every commit |
| **Consistency** | `tests/contract/consistency_check.mjs` | every commit |
| **Safety invariants** | `tests/contract/safety_invariants.mjs` | every commit |
| Integration | `tests/integration/` | every commit |
| Failure | `tests/failure/` | nightly + pre-release |
| Load | `tests/load/` | pre-release |
| E2E demo | `tests/e2e/` | pre-release |

## 2. Contract tests (already runnable)

`tests/contract/validate_examples.mjs` validates every documented example against its schema and
fails if any schema lacks example coverage. It is dependency-free so it runs in the specification
phase, before any application code exists.

**Verified during v0.3 authoring:** the test was run against a reconstruction of the v0.2 telemetry
example and correctly rejected it with six violations — three missing envelope fields and the three
missing measurements (`voltageV`, `torqueNm`, `operationRatePct`) that constituted GAP-001. The
regression probe was then removed, leaving 8 examples passing. This is the evidence that the test
catches the original defect rather than passing vacuously.

Additional contract tests to add with implementation:
- **CT-01** every emitted event validates against its schema at runtime in integration tests;
- **CT-02** `quality.overall` equals the value derived from the flags for every fixture combination;
- **CT-03** generated C#/Python/TypeScript DTOs round-trip every example without loss;
- **CT-04** proto backward compatibility against the previous tag;
- **CT-05** every OpenAPI response, including errors, validates against the contract.

## 3. Safety Supervisor gate tests (table-driven)

Required by `TEST_STRATEGY.md`. For each of the 13 gates: a pass case, a fail case, a boundary case,
and an invalid-input case.

**Invariants asserted across the whole table:**

| ID | Invariant |
|---|---|
| SG-INV-1 | No combination yields `ACCEPT` unless **all 13** gates pass (AC-015) |
| SG-INV-2 | Ambiguous or invalid required input yields rejection/fallback, **never** acceptance |
| SG-INV-3 | Every decision carries ≥ 1 reason code and a correlation ID (D-04) |
| SG-INV-4 | Every decision records **all** gate results, not only the first failure |
| SG-INV-5 | `OOD_HIGH` always rejects; no reduced-authority path exists (AC-005) |

Boundary cases that must exist explicitly, because they are where off-by-one errors live:
prediction age exactly 10 s; telemetry age exactly 2 s; `oodScore` exactly at threshold;
`confidence` exactly at threshold; `validSampleRatio` exactly 0.8; delta exactly 10 pp;
cumulative budget exactly 25 pp; target exactly 60 % and exactly 100 %.

## 4. Failure test specifications

Each defines setup, trigger, expected transitions, thresholds, and assertions.
Every run records git commit, profile, machine specification, duration, configuration, observed
metrics, and pass/fail against its AC (`RUNBOOK_AND_FAILURE_TESTS.md`).

### FAIL-AI-001 — inference process death → AC-004
**Setup:** 20 equipment, `NORMAL`, mode `AI_ASSISTED`, gates passing.
**Trigger:** `SIGKILL` the prediction service.
**Expected:** predictions stop; within 10 s + skew the Supervisor rejects on `PREDICTION_STALE`;
mode → `SAFE_FALLBACK` (M3); rate driven to 60 %; Control Service stays healthy.
**Assert:** `fallback_active == 1` within 12 s · `ai_recommendation_total{reason="PREDICTION_STALE"}`
increases · Control Service `up == 1` throughout · applied rate reaches 60 ± 1 % within 15 s ·
**no command failure**.

### FAIL-SUP-001 — Safety Supervisor death → AC-011 *(new in v0.3, the headline test)*
**Setup:** as above.
**Trigger:** `SIGKILL` the Safety Supervisor.
**Expected:** heartbeats stop; at 12 s ± 1 s the Control Service watchdog fires, increments
`controlEpoch`, writes the fallback rate, publishes an outcome with `SUPERVISOR_SILENT` and
`autonomous: true`; mode → `SAFE_FALLBACK` (M4).
**Assert:** `watchdog_fallback_total` increases exactly once per equipment · `control_epoch`
increments · applied rate reaches 60 ± 1 % within 15 s · outcome carries `autonomous: true` and
`decisionId: null` · **equipment is never left above the fallback rate**.
**This is the test that did not exist in v0.2 and that the architecture was changed to pass.**

### FAIL-SUP-002 — late command after watchdog fallback → AC-013 *(new)*
**Setup:** mode `AI_ASSISTED`; inject a network delay on the Supervisor→Control path.
**Trigger:** Supervisor issues a command at T0; delay it 16 s; watchdog fires at T+12 s.
**Expected:** the delayed command arrives carrying the **pre-fallback** epoch and is rejected.
**Assert:** response `status=FENCED`, `reasonCode=COMMAND_EPOCH_STALE` ·
`command_epoch_stale_total` increases · **applied rate does not move off 60 %** ·
the Supervisor re-acquires a lease and re-derives rather than resending.

### FAIL-PLAT-001 — total platform stop → AC-012 *(new)*
**Setup:** equipment `RUNNING` at 100 %.
**Trigger:** stop every platform service including the Control Service.
**Expected:** no setpoint refresh; at 30 s the equipment dead-man reverts to its safe default.
**Assert:** applied rate reaches the safe default within 35 s · `deadman_reverts_total` increases ·
proves L3 independence.

### FAIL-OT-001 — protocol disconnect/restore → AC-002
**Trigger:** stop the OPC UA / Modbus endpoint for 30 s, then restore.
**Assert:** state → `OFFLINE` within 3 s · `protocol_connected == 0` · reconnect within 10 s
**with no process restart** · `reconnect_total` increases · telemetry resumes · `SEQUENCE_GAP`
raised for the gap.

### FAIL-DATA-001 — stale telemetry → AC-005
**Trigger:** freeze telemetry for 5 s while the Supervisor remains up.
**Assert:** rejection with `TELEMETRY_STALE` · mode → `SAFE_FALLBACK` · no command applied from
stale data.

### FAIL-OOD-001 — OOD profile → AC-005/AC-019
**Trigger:** activate `OOD_PROFILE` (rpm held at 1800 while rate is commanded to 60 %).
**Assert:** every value remains in range so a pure range check would pass · `oodScore` exceeds
threshold · **hard reject** `OOD_HIGH` · `prediction_ood_total` increases · **no** reduced-authority
acceptance occurs.

### FAIL-CMD-001 — duplicate command → AC-007
**Trigger:** deliver the same `commandId`/idempotency key twice.
**Assert:** first `APPLIED`, second `DUPLICATE` with the **prior** result returned ·
`duplicate_command_total` increases · equipment state changes **at most once** (D-01).

### FAIL-KAFKA-001 — broker restart and replay → AC-003
**Trigger:** restart the broker; then replay a known offset range into the projector group.
**Assert:** gateway buffers and drops **oldest** only · `telemetry_dropped_total` is counted, not
silent · consumers resume ≤ 30 s · projections rebuild byte-identically · **the Supervisor issues no
command as a result of replay** · control unaffected throughout (F06, F11).

### FAIL-MODEL-001 — quarantine propagation → AC-033 *(new)*
**Trigger:** quarantine the running model in MLflow.
**Assert:** `ModelAuthorization` published with `acks=all` **before** the registry transition reports
complete · the Supervisor rejects its predictions within **2 s** (L-10) with `MODEL_QUARANTINED` ·
mode → `SAFE_FALLBACK`.

### FAIL-MODEL-002 — publisher death → AC-034 *(new)*
**Trigger:** `SIGKILL` `mlops-publisher`; leave Kafka healthy.
**Expected:** watermarks stop while Kafka stays reachable and quiet.
**Assert:** `authorization_watermark_age_seconds` grows · at 90 s **all models become unauthorized**
· `MODEL_AUTHORIZATION_STALE` · fallback.
**This test exists because silence is otherwise indistinguishable from "nothing changed."**

### FAIL-MODEL-003 — canary rollback → AC-010 [P1]
**Trigger:** deploy a known-bad candidate to a canary cohort.
**Assert:** fallback rate for the cohort rises > 20 pp above the Production baseline within 15 min ·
rollback trigger fires · quarantine propagates · cohort returns to the Production model.

### FAIL-DB-001 — PostgreSQL outage *(new)*
**Trigger:** stop PostgreSQL for 60 s.
**Assert:** new commands rejected · **the watchdog still fires and still applies fallback** (it needs
only static config) · Operations API returns 503 · no data corruption on recovery · projections
resume from committed offsets.

### FAIL-CLOCK-001 — clock skew *(new)*
**Trigger:** step one host's clock by +2 s.
**Assert:** `clock_skew_seconds` exceeds 0.25 and alerts · freshness gates apply the skew budget on
the permissive side · **no stale data is accepted as fresh** (the unacceptable consequence in F31).

## 5. Load test specifications

| ID | Scenario | Asserts |
|---|---|---|
| LOAD-001 | 20 equipment @ 100 ms for 30 min | T-01 demo, L-01, no dropped telemetry, no gaps |
| LOAD-002 | 250 equipment @ 100 ms for 15 min | T-01 load, buffer depth bounded, lag bounded |
| LOAD-003 | Sustained command rate | T-04, L-06 |
| LOAD-004 | End-to-end latency under load | **L-07 P95 ≤ 1 200 ms** |
| LOAD-005 | Projection rebuild from 6 h telemetry | R-08 ≤ 10 min |

## 5a. Phase 1 unit tests — implemented

`src/dotnet/EquipmentSimulator.Tests/` (44 tests, `dotnet test src/dotnet/Mair.sln`).

| File | Proves |
|---|---|
| `FaultProfileSignatureTests.cs` | **AC-018** — the documented signature of all 11 fault profiles |
| `DeterminismTests.cs` | **AC-018 / PROP-03** — identical seed ⇒ bit-identical telemetry; different seed ⇒ different telemetry; `sequence` monotonicity and the restart reset |
| `OodProfileTests.cs` | **AC-019** — the rpm↔rate relationship is broken while every value stays in range and no range check fires |
| `ProtectiveConditionTests.cs` | **AC-020** — the four sensed protective conditions plus `STOP_REQUIRED`, `FAULT` latching, operator reset, and the zero-external-dependency proof that L3 survives total platform loss |
| `StateMachineTests.cs` | T1–T12, the three forbidden transitions, and "`RUNNING` is the only AI-eligible state" |
| `ActuationTests.cs` | slew limit, dead-man revert, reject-never-clamp, setpoint idempotency, fail-closed configuration, demo-only fault injection |

The determinism suite deliberately contains a **negative** case: without
`DifferentSeed_ProducesDifferentTelemetry`, an implementation that emitted no noise at all would
satisfy PROP-03 trivially.

## 6. Property-based tests

| ID | Property |
|---|---|
| PROP-01 | No admissible command sequence moves the rate outside 60–100 % |
| PROP-02 | No sequence exceeds 10 pp in one decision or 25 pp per 60 s window |
| PROP-03 | Identical seed ⇒ bit-identical simulator telemetry (NFR-010) |
| PROP-04 | Identical telemetry range ⇒ byte-identical features regardless of restart (NFR-013) |
| PROP-05 | Replaying any decision stream produces no additional equipment writes |
| PROP-06 | For any flag combination, `quality.overall` equals the derivation rule |

## 7. Traceability

Every AC in `ACCEPTANCE_CRITERIA.md` maps to at least one specification here. CI fails if an AC has
no mapped test, and the mapping is asserted by a test rather than maintained by hand — an unmapped
AC is a build failure, not a documentation gap.
