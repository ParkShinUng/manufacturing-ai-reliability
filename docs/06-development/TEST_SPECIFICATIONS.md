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
**Trigger:** stop the endpoint the **gateway** reads — OPC UA, or the Modbus read-only listener
`5020` — for 30 s, then restore. The Modbus write listener `5021` is Control Service-facing and is
not part of gateway reconnect (OD-003); its loss is a control-path failure, not a telemetry one.
**Assert:** state → `OFFLINE` within 3 s · `protocol_connected == 0` · reconnect within 10 s
**with no process restart** · `reconnect_total` increases · telemetry resumes · `SEQUENCE_GAP`
raised for the gap.

**Automated 2026-09-21** — `EndpointOutageTests`, both protocols, real servers torn down and rebuilt
on the same port while the model clock keeps stepping. The outage is **10 s**, not 30: the reconnect
bound is set by the backoff, which reaches its 8 s cap (±20 %) after 7.75 s of failures, so any
longer outage exercises the same worst case. In Phase 2 the `OFFLINE` observable is
`protocol_connected`; the `factory.equipment-states.v1` record is bound with the egress in Phase 3.

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

### FAIL-KAFKA-001 — broker restart and replay mechanics → AC-045 *(Phase 3)*
**Trigger:** restart the broker; then rewind a **replay-eligible** consumer group over a known
offset range.
**Assert:** gateway buffers and drops **oldest** only · `telemetry_dropped_total` is counted, not
silent · consumers resume ≤ 30 s (F06) · the rewound group consumes **exactly** the records in that
range, in key order · offsets commit only **after** successful processing · a rewind attempt against
`cg.safety-supervisor.v1` is **refused**, not silently honoured (DEC-002, ADR-0012).

*Split 2026-09-28 by OD-007.* This test previously also asserted that read models rebuild
identically and that the Supervisor issues no command — neither of which exists in Phase 3. Those
are `PROJ-001` and `FAIL-KAFKA-002` below.

### FAIL-KAFKA-002 — replay moves no equipment → AC-003 *(Phase 6)*
**Setup:** Supervisor running, mode `AI_ASSISTED`, gates passing, a known range of historical
predictions on `factory.predictions.v1`.
**Trigger:** replay that range.
**Assert:** the Supervisor issues **no** command as a result (F11) · no `controlEpoch` advances ·
the applied rate does not move · any late command carrying a pre-replay epoch is refused
`COMMAND_EPOCH_STALE` · the audit trail shows the replayed records were consumed and **inert**.

**This is the whole safety claim, on one criterion.** It is the test `FAILURE_MODEL.md` names for
"replay cannot move equipment"; the mechanics tests above cannot stand in for it.

### PROJ-001 — read models rebuild from a replay → AC-046 *(Phase 4)*
**Trigger:** replay a known offset range into the projection consumer groups.
**Assert:** every read model is **byte-identical** to its pre-replay content · the rebuild is
idempotent under a second replay of the same range · no projection writes outside its own tables.

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

## 4a. Phase 2 gateway test specifications

Added 2026-09-16. The Definition-of-Ready check at Phase 2 initialisation found that four of the
five Phase 2 acceptance criteria had **no** specification here, while §7 claimed every AC was
mapped. §7 was aspirational; these close the gap.

All four are proven against the **egress port** (`EDGE_GATEWAY.md` §5.1), not against Kafka.
Kafka binding is FR-010 and is proven in Phase 3.

### OT-001 — sustained canonical emission → AC-001
**Setup:** 20 simulated equipment, mixed fault profiles, both protocols in use, gateway connected,
recording egress sink.
**Trigger:** run for 30 minutes at the 100 ms cadence.
**Expected:** continuous canonical telemetry; no unhandled exception in simulator or gateway.
**Assert:** every emitted record validates against `telemetry.schema.json` · all seven measurement
keys present on every record (nullable, but present) · `equipmentId`, `eventTimeUtc`,
`ingestTimeUtc`, `occurredAtUtc`, `sequence`, `schemaVersion`, `producer`, `quality`,
`correlationId` present on every record (FR-006) · records seen for all 20 equipment throughout ·
**zero unhandled exceptions** · `local_buffer_depth` bounded · `telemetry_dropped_total == 0` with
a healthy sink.

Proven in two halves. `SustainedEmissionTests` covers the 30 minutes and all 360 000 records
in-process, the simulator being tick-driven. `LoadScenarioTests.Ac001_*` runs the same assertions on
every record through the **real** servers, clients and poll loops, per protocol, for a few seconds
in every suite run (COD-P2-006); the 30 wall-clock minutes on that path are `LOAD-001`.

### OT-002 — cross-protocol agreement → AC-021
**Setup:** the same equipment exposed on **both** OPC UA and Modbus; gateway reads both.
**Trigger:** collect paired samples over 5 minutes across the full rate range, including a slew.
**Expected:** both paths normalise to the same engineering values.
**Assert:** for every paired sample and every channel, the two protocols agree **within the
channel's documented resolution** (`EQUIPMENT_MODEL_AND_STATE.md` §1.2: 0.1 rpm, 0.1 N·m, 0.01 A,
0.1 V, 0.1 °C, 0.01 mm/s, 0.1 %) · the 32-bit fields decode **high word first** · `sourceEpochMs`
read across `+20/+21` matches the OPC UA value · `statusBitmap` at `+22` is read and matches.

Asserted on **both** OPC UA routes — the batch read and the §1.3 subscription the gateway ships
(COD-P2-005). They share the frame builder but reach it through different machinery, so agreement
on one is not evidence about the other.

This is the test that catches scaling and word-order errors, which is why it compares engineering
values rather than raw registers: a word-order mistake in a scaled integer produces a plausible
wrong magnitude, and only the second protocol disagrees with it.

### OT-003 — dead sensor is null, never substituted → AC-022
**Setup:** one equipment per sensor fault profile (`SENSOR_DROPOUT`, `SENSOR_FREEZE`),
on each protocol.
**Trigger:** run until each fault is active for at least 60 s.
**Expected:** the affected channel is emitted as `null` with a quality flag.
**Assert:** the channel is `null` — **never 0, never last-known, never interpolated** (ADR-0018,
DEC-008) · the key is still present · an accompanying flag from the closed vocabulary names that
channel · `quality.overall` equals the derivation rule for the resulting flag set · the record still
validates against `telemetry.schema.json` · `quality_flags_total{flag,channel}` increases.

**Negative case, required:** a run with **no** sensor fault emits **no** flags and
`quality.overall == GOOD`. Without it an implementation that flagged everything would pass.

### OT-004 — no gap goes undetected → AC-023
**Setup:** 20 equipment, healthy, gateway connected. **Run the whole matrix on both protocols** —
OD-004 exists because the two disagreed, so agreement is what is being proven.
**Trigger:** the five cases below.
**Expected:** the paired-signal invariant in `EDGE_GATEWAY.md` §14, identically on OPC UA and Modbus.

| # | `sequence` | `sourceEpochMs` | Expected |
|---|---|---|---|
| 1 | increments by > 1 | advances normally | `SEQUENCE_GAP`, counted |
| 2 | unchanged | unchanged | `DUPLICATE_SUSPECTED`, **not** a gap |
| 3 | resets | resets | **not** a gap — restart or 2^32 ms wrap; tracking restarts |
| 4 | resets | advances normally | `SEQUENCE_GAP` — the signals disagree, so it is not a restart |
| 5 | advances normally | resets | `SEQUENCE_GAP` — same, mirrored |

**Assert:** `SEQUENCE_GAP` on the `__event__` channel exactly in cases 1, 4 and 5 ·
`telemetry_sequence_gaps_total` increases by exactly that count · **no gap inside a continuous
source epoch is missed** · the OPC UA and Modbus paths produce the **same** flags for the same case.

Cases 3, 4 and 5 are the point. A detector that flags every `sequence` decrease passes case 1 and
cries wolf on every restart; one that suppresses every decrease passes cases 1 and 3 and **hides
real loss** in cases 4 and 5. Only the paired signal separates them, which is why OD-004 had to add
`SourceEpochMs` to the OPC UA address space before this test could exist at all.

## 4b. Phase 3 Kafka test specifications

Added 2026-09-28. `AC-026` and `AC-027` were listed in §7a as unspecified, and the Definition-of-Ready
check made writing them a condition of starting Phase 3.

### KAFKA-001 — topics exist as documented, and the bootstrap is idempotent → AC-026
**Setup:** a broker with no `factory.*` topics.
**Trigger:** run the bootstrap job; run it a **second** time unchanged; then run it once more with
the partition count of one topic altered in its input.
**Expected:** the first run creates every topic in the §2 register; the second changes nothing; the
third **fails loudly**.
**Assert:** for all 7 topics the partition count, replication factor, retention and cleanup policy
equal the register — including `factory.equipment-states.v1` at `compact` with **no** deletion
(OD-008) · the second run makes **no** change and reports success · the third run **refuses** and
exits non-zero with the topic named, because a changed partition count silently re-keys a keyed
topic and destroys per-key ordering (§4) · no topic is auto-created by a producer or consumer at any
point in the run.

**Immutability is ours to enforce, not Kafka's.** `AdminClient.CreatePartitionsAsync` exists and the
broker will increase a partition count on request (ADR-0021). The bootstrap therefore describes the
topic and fails **before** any mutating call, and the test asserts both halves: the run exits
non-zero **and** the live topic still has its original partition count afterwards.

### KAFKA-002 — a schema-invalid record is DLQ'd on the first attempt → AC-027
**Setup:** a real broker and topic, a consumer built on the **shared consume-validate-DLQ
component** (OD-009) with a downstream handler that records every invocation.
**Trigger:** produce one record that fails its JSON Schema, followed by one valid record.
**Expected:** the invalid record is routed to `<topic>.dlq` immediately; the valid one is processed
normally.
**Assert:** the DLQ record carries the **original payload** and every required header (§9) · it is
produced on the **first** attempt — the consumer's retry counter for it is zero · the source offset
is committed **only after** the DLQ produce succeeds, so a crash in between replays the record
rather than losing it · the downstream handler was **not** invoked for the invalid record · nothing
redrives automatically · the following valid record is processed, proving the consumer did not stall.

**Negative case, required:** a valid record must **not** reach the DLQ. Without it, a component that
DLQ'd everything would pass.

**Automated 2026-09-29** — `ContractConsumerTests`, real broker, the production `ContractConsumer`.
Beyond the specification above it also asserts that an **unparseable** record is DLQ'd rather than
crashing the consumer (ADR-0022 condition 5), that a handler failure is retried on §9's 1 s / 2 s
schedule and DLQ'd after the third attempt, and that when the DLQ produce itself **fails** the
source offset is not committed and the record is redelivered.

`AC-027` proves routing, not validator correctness, so the validator has its own suite,
`ContractSchemaTests` (ADR-0022 conditions 1 and 2): every documented example validates against the
schema `_manifest.json` pairs it with; each of the 23 validation keywords the contract schemas use
rejects an invalid case **and** accepts a valid one; and an inventory test fails if a schema starts
using a keyword with no case.

## 5. Load test specifications

| ID | Scenario | Asserts |
|---|---|---|
| LOAD-001 | 20 equipment @ 100 ms for 30 min | see §5a. Shares its harness with `OT-001` (AC-001) |
| LOAD-002 | 250 equipment @ 100 ms for 15 min | T-01 load, buffer depth bounded, lag bounded |
| LOAD-003 | Sustained command rate | T-04, L-06 |
| LOAD-004 | End-to-end latency under load | **L-07 P95 ≤ 1 200 ms** |
| LOAD-005 | Projection rebuild from 6 h telemetry | R-08 ≤ 10 min |

### LOAD-001 in detail — amended 2026-09-21 by OD-005

The original wording was **"no dropped telemetry, no gaps"**. That is not achievable by Modbus
latest-register polling at the same cadence as the producer, and the first real run proved it:
73 % coverage, 161 gaps, 162 duplicate reads over 60 s — the signature of two equal-period clocks
drifting past each other, with no other defect needed to explain it.

Requiring the impossible would have left one of two outcomes: a permanently red criterion, or
someone quietly relaxing the detector until it went green. The criterion is therefore what the
design can honestly deliver.

**Report** — `scripts/run-load-001.mjs` writes all of these to `reports/load/`:
`records` · `distinct source sequences` · `duplicates` · `coverage %` · `sequence gaps` ·
`read failures` · `loop overruns` · `buffer drops`.

**Assert, per protocol:**

| Path | Requirement |
|---|---|
| OPC UA subscription | **zero** sequence gaps after start-up warm-up · bounded incomplete groups at start-up only · no buffer drops |
| Modbus polling | **every** skipped sequence flagged — no silent loss · duplicates counted · **zero** read failures on a healthy run · coverage measured and reported, not asserted to be 100 % |

Coverage is a **measurement**, not a threshold. Setting a number before understanding what the
platform does on a representative deployment profile would be inventing a target, and a developer
laptop is not that profile.

**Run per protocol, one after the other** (amended 2026-09-21, COD-P2-007). Both in one process
measured the rig — forty loops and twenty sessions on one laptop halved the achieved poll rate —
and a gateway reads each machine over one protocol anyway. OPC UA warm-up is **measured**: the
zero-gap window opens 1 s after the last loop's first record, not after a fixed allowance.

The Modbus "zero read failures" requirement failed at 20 equipment until **OD-006** replaced the
NModbus client, whose reads blocked a pool thread each; it passes on the async reader with the
pool at its default size. The "no other defect needed to explain it" above held at one equipment;
at twenty, OD-006 was a second cause.

OPC UA coverage counts from the first model step, so it includes the seconds twenty sessions take
to connect. That is start-up, not loss — the gap count after warm-up is the loss figure.

**First official run, 2026-09-21 — FAILED, diagnosed, rig corrected.** Modbus passed (0 read
failures, every one of 66 skips flagged). OPC UA reported **35 gaps** after warm-up against a
requirement of zero. A 30-minute OPC UA-only diagnostic reproduced it with 60 gaps and showed the
cause:

| | |
|---|---|
| gaps within 3 s after a model-clock burst | **60 / 60** |
| steps the gateway's assembler dropped | 0 |
| late values discarded | 0 |
| longest model-clock stall | 2 049 ms |

The rig's model clock caught up after each stall by issuing steps about a millisecond apart. §1.3
states that steps closer than the 50 ms sampling interval cannot all be sampled, and every gap sat
on such a burst. The subscription lost nothing it could have seen. No machine emits a burst of
catch-up steps, so the rig now **re-anchors** after a stall, as the gateway's poll loop does. The
requirement was not relaxed. The run is repeated on the corrected rig, and the report records
`model_clock_bursts`, `longest_step_gap_ms` and gap-to-burst correlation, so a recurrence is
attributable from the report alone.

**Second official run, 2026-09-22 — PASSED.** Commit `249948a`, clean tree, 30 min per protocol,
20 equipment. The report lives under the git-ignored `reports/load/`, so the figures are copied
here:

| | Modbus polling | OPC UA subscription |
|---|---|---|
| records | 360 020 | 359 916 |
| distinct sequences | 356 386 | 359 916 |
| duplicates | 3 634 | 0 |
| coverage | 99.0 % | 100.0 % |
| sequence gaps | 3 634, **every one flagged**, all single | **0** |
| read failures | **0** | 0 |
| loop overruns | 0 | 20, one per loop at start-up |
| buffer drops | 0 | 0 |
| assembler drops / late values | — | 0 / 0 |
| model-clock bursts · longest step gap | 0 · 153 ms | 0 · 128 ms |

The Modbus figures are equal-cadence phase drift, exactly as OD-005 describes: gaps and duplicates
match one for one, and none is silent. These are measurements of one laptop on one commit, not the
`NON_FUNCTIONAL_REQUIREMENTS.md` targets, which stay `TARGET (unmeasured)` (NFR-005).

## 5a. Phase 1 unit tests — implemented

`src/dotnet/EquipmentSimulator.Tests/` (44 tests, `dotnet test src/dotnet/Mair.sln`).

| File | Proves |
|---|---|
| `FaultProfileSignatureTests.cs` | **AC-018** — the documented signature of all 11 fault profiles |
| `ModbusCodecTests.cs`, `ModbusDataStoreTests.cs`, `ModbusOverTheWireTests.cs` | Modbus half of **AC-021**, the §2.4 response table, and OD-003's read-only listener — on real sockets |
| `QualityDerivationTests.cs` | **PROP-06** — `quality.overall` for every flag combination |
| `NormalisationTests.cs` | **AC-022**, **AC-023** and the egress buffer |
| `DeterminismTests.cs` | **AC-018 / PROP-03** — identical seed ⇒ bit-identical telemetry; different seed ⇒ different telemetry; `sequence` monotonicity and the restart reset |
| `OodProfileTests.cs` | **AC-019** — the rpm↔rate relationship is broken while every value stays in range and no range check fires |
| `ProtectiveConditionTests.cs` | **AC-020** — the four sensed protective conditions plus `STOP_REQUIRED`, `FAULT` latching, operator reset, and the zero-external-dependency proof that L3 survives total platform loss |
| `StateMachineTests.cs` | T1–T12, the three forbidden transitions, and "`RUNNING` is the only AI-eligible state" |
| `ActuationTests.cs` | slew limit, dead-man revert, reject-never-clamp, setpoint idempotency, fail-closed configuration, demo-only fault injection |

The determinism suite deliberately contains a **negative** case: without
`DifferentSeed_ProducesDifferentTelemetry`, an implementation that emitted no noise at all would
satisfy PROP-03 trivially.

## 5b. Phase 2 tests — implemented

`src/dotnet/EdgeGateway.Tests/`.

| File | Proves |
|---|---|
| `SustainedEmissionTests.cs`, `LoadScenarioTests.cs` | **AC-001** — in-process over 30 simulated minutes, and on the real protocol path per protocol; `LOAD-001` |
| `EndpointOutageTests.cs` | **AC-002** — `FAIL-OT-001` on both protocols with real endpoints |
| `PollLoopTests.cs` | **AC-002** — reconnect without restart, backoff, and egress failures isolated from protocol health |
| `ModbusFramingTests.cs` | the gateway's FC04 reader (OD-006) against a misbehaving server: request framing, trickled bytes, every malformed-response case, exception PDUs, timeouts, and a late answer that must never be read as the next reply |
| `CrossProtocolTests.cs` | **AC-021** — on the batch read and the subscription |
| `NormalisationTests.cs`, `QualityDerivationTests.cs` | **AC-022** — null plus a flag, never a substitute; `quality.overall` for every flag combination |
| `SequenceEpochMatrixTests.cs`, `NormalisationTests.cs` | **AC-023** — the five-case paired-signal matrix on both protocols |
| `OpcUaSampleAssemblerTests.cs`, `OpcUaSampleCoherenceTests.cs` | the §1.3 assembly rule: deterministic uneven queues, straddles, discards and late values, then the same over the wire at 120, 60 and 20 ms steps; §1.4 status translation reaching the event |
| `PollVsSubscribeTests.cs` | the OD-005 measurement |

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

**That assertion test does not exist yet.** Until it does, this section states an intention rather
than an enforced property, and it has already been wrong once: at Phase 2 initialisation four of the
five Phase 2 ACs had no specification here while this paragraph claimed otherwise (§4a). Building
the mapping check is Phase 2 work, so that the claim stops depending on whoever last read the file.

## 7a. Specifications not yet written

*Added 2026-09-18, when the traceability check in §7 was finally built and found that **25 of 44**
criteria had no specification — not the four the Phase 2 Definition-of-Ready check had spotted by
hand. §7 had been asserting the opposite since v0.3.*

Every criterion below belongs to a phase that has **not started**. The check
(`tests/contract/ac_traceability.mjs`) enforces three things, and the second is what stops this list
becoming a permanent excuse:

1. every AC is either covered by a specification or listed here;
2. **every AC listed here belongs to a phase that has not started** — so the list must shrink as a
   phase begins, or the build fails;
3. nothing is both covered and listed, so a stale entry cannot linger.

| AC | Phase |
|---|---|
| AC-006, AC-028, AC-029, AC-030 | 4 |
| AC-008, AC-024, AC-025, AC-032 | 5 |
| AC-016, AC-017 | 6 |
| AC-014, AC-038, AC-039, AC-040 | 7 |
| AC-035, AC-036, AC-041, AC-044 | 8 |
| AC-043 | 10 |
| AC-009, AC-031, AC-037 | 11 |
| AC-042 | 12 |

**`AC-039` was assigned to no phase at all.** `IMPLEMENTATION_PLAN.md` names every other criterion in
some phase's proof list; this one appeared in none of them, so nobody had been told to prove it —
and it verifies **FR-030 and FR-035**, both P0: no AI service holds equipment credentials, and no
component other than the Control Service writes to equipment. It is assigned to **Phase 7**, where
the Control Service and therefore the write path exist. Part of it is already provable — OD-003's
read-only Modbus listener refuses writes with `0x01` — and that clause is recorded on the criterion.
