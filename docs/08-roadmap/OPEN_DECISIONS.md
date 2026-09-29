# Baseline Decisions — Resolved for v0.2

The following baseline choices are now normative unless changed through the documentation-first workflow.

1. **Equipment model**: variable-speed motor-driven conveyor/drive unit with bearing degradation model.
2. **Demo scale**: 20 equipment instances at 100 ms telemetry. Load profile is configurable up to 250 virtual equipment instances; any published throughput claim must be measured.
3. **Telemetry cadence**: raw telemetry every 100 ms per equipment.
4. **Feature/inference cadence**: maintain a 60-second feature window, produce 1-second aggregate features, run inference every 5 seconds per equipment.
5. **OOD baseline**: training-distribution z-score envelope + hard feature validity/range checks. More advanced OOD methods require evidence and an ADR.
6. **Operational persistence**: PostgreSQL stores 1-second aggregates, predictions, safety decisions, command results, fault-injection history, and model-deployment metadata. Raw 100 ms telemetry is not duplicated into PostgreSQL in baseline; Kafka provides short-term replay retention.
7. **Kafka local topology**: one KRaft broker for local development. Multi-broker HA is not a baseline claim; it may be added only for a documented HA experiment.
8. **Model rollout**: candidate models first run in shadow mode on the same telemetry with a separate consumer group/topic. If approved, AI authority can be canaried by an explicit simulator-equipment cohort. Candidate output never gains control authority by network percentage alone.
9. **Command transport**: Safety Supervisor → Control Service uses gRPC, not Kafka. Kafka receives audit/outcome events but is not a synchronous dependency of command execution.
10. **Manual / operator-originated control: DEFERRED.** v0.2 allowed "explicit human/manual mode"
    to originate a production command. That allowance is **removed** in v0.3 (DEC-004): it was a
    second command origin with no state, authorization, interlock, or relationship to the Safety
    Supervisor, and therefore a documented bypass of the deterministic safety path. In the v0.3
    baseline the Safety Supervisor is the only production command origin. Manual operator control
    has **no contract surface** in v0.3 and may be introduced later only through the normal
    documentation-first workflow with its own ADR, state machine, authorization model, and
    acceptance criteria. Whether operators should ever command equipment directly is a **product**
    question carried to human review as HD-002.

11. **Default simulator control profile**: nominal operation rate 100%, fallback rate 60%, AI-authorized range 60–100%, max rate change per accepted recommendation 10 percentage points, telemetry freshness 2 seconds, prediction TTL 10 seconds. These are simulator demonstration values, not real industrial safety limits.

---

# OD-001 — RESOLVED 2026-09-15 — no fault profile can cause drive-tracking failure

> **Decision: option A.** The product owner added a `DRIVE_STUCK` profile, making it 11. AC-018,
> `EQUIPMENT_MODEL_AND_STATE.md` §2, and the tests are updated; the demo-only `InjectDriveLag()` hook
> has been **removed**, because the condition now has a real cause and a test hook that duplicates a
> profile is a second way for the two to drift apart.

`EQUIPMENT_MODEL_AND_STATE.md` §3.3 lists, as a degradation condition (`T7`, `RUNNING → DEGRADED`):

> observed `operationRatePct` deviates from the slew-limited expected rate by > 10 pp for
> > `slew_grace` 5 s (i.e. the drive is not tracking its setpoint)

**None of the ten fault profiles in §2 can produce this.** They act on bearing health, cooling,
load, single sensor channels, and the transport. Not one makes the *drive* fail to follow its own
slew-limited response. So the condition is documented, is now implemented, and has no modelled
cause — the only thing that can currently exercise it is a demo-only injection.

This was found because the first implementation made the condition **mathematically unreachable**
(it compared the applied rate against a variable that was assigned the applied rate). Codex caught
it as `COD-VFY-004`. Fixing the tautology exposed the underlying gap: a safety-relevant condition
with no cause in the model is untestable by anything except a test hook, and a condition that only a
test hook can reach is not being validated by the scenarios the demo actually runs.

**Options**

| | Option | Consequence |
|---|---|---|
| A | Add a `DRIVE_STUCK` (or `ACTUATOR_LAG`) profile to §2, making it 11 profiles | AC-018 changes from "10 profiles" to 11; the condition gains a real cause and a real test; closest to how a VFD actually fails |
| B | Keep 10 profiles and mark the condition as reachable only on **real** equipment, with the simulator exercising it through the demo injection | no contract change; the condition stays unproven in the demo, and `slew_grace` stays an untuned constant |
| C | Remove the condition from §3.3 | smallest model, but deletes the only check that would notice a drive ignoring the platform — which is precisely the failure the three-layer authority model exists for |

**Recommendation: A.** C is wrong for this project — the whole point of `T7` here is to notice that
the equipment is not doing what it was told. B leaves a safety condition that nothing in the demo
ever triggers.

**Status: RESOLVED — option A, 2026-09-15.** `DRIVE_STUCK` freezes `r_applied` at `T_stuck`
(demo default 60 s), so `T7` fires from a documented profile rather than a test hook. The
expected-rate integrator stays independent of the applied rate, which is what makes the condition
measurable at all.

---

# OD-002 — RESOLVED 2026-09-15 — the protective thresholds are above anything the model can produce

> **Decision: option B.** Fault severity was raised; the protective limits in §3.4 are unchanged.

`EQUIPMENT_MODEL_AND_STATE.md` §3.4 defines three sensed protective conditions. §1.3 and §2 define
what the simulator can actually generate. They do not meet.

| Condition | Threshold (§3.4) | Maximum any profile can produce (§1.3, §2) | Reachable? |
|---|---|---|---|
| `temperatureC` > 120 °C | 120.0 | **120.46 °C** — `COOLING_DEGRADATION`, `c` capped at 0.9, full rate: `22 + 32/(1 − 0.675)` | barely: 0.46 °C of margin, ~20 min from ambient |
| `vibrationRms` > 25.0 mm/s | 25.0 | **15.4 mm/s** — `BEARING_DEGRADATION` at `h = 1`: `2.2 · 1 · (1 + 6)` | **no** |
| `currentA` > 32 A for > 1 s | 32.0 | **19.2 A** — `OVERLOAD` at full rate: `12 · 1.6` | **not as real current.** Only `SENSOR_DRIFT` on `currentA` crosses it, after ~25 min, and that is a sensor fault rather than an over-current |

### Why this matters more than a threshold mismatch

L3 equipment self-protection is the layer `DEC-001` and `ADR-0011` describe as the one that still
works when the entire platform is down. It is the innermost defence in the three-layer authority
model, and the headline reliability claim rests on it.

**Nothing the demo runs can trigger two of its three sensed conditions.** The Phase 1 tests reach
for a demo injection to prove AC-020 not because injection is convenient but because no fault
profile gets there. A protective layer that only a test hook can exercise has not been validated by
any scenario a reviewer will ever see running.

This was found by trying to replace an injected over-current test with a physics-driven one
(Codex `COD-VFY-001` residual, round 2).

### Options

| | Option | Consequence |
|---|---|---|
| A | Lower the thresholds into the model's range — e.g. vibration 25 → 14 mm/s, current 32 → 18 A | smallest change; every condition becomes reachable by an existing profile. But the degradation thresholds (12 mm/s, 95 °C) then sit very close to the protective ones, so `DEGRADED` and `FAULT` stop being distinguishable stages |
| B | Extend the profiles so they can reach the current thresholds — a severe `BEARING_DEGRADATION` tier, a heavier `OVERLOAD` factor | keeps the safety limits as stated and preserves the gap between `DEGRADED` and `FAULT`; changes §2 constants and possibly AC-018 |
| C | Accept that the thresholds are aspirational limits the simulator never reaches, and document the tests as injection-driven by design | no constant changes; permanently leaves the innermost safety layer unexercised by any scenario, which contradicts what `AC-020` is for |

**Recommendation: B.** The thresholds in §3.4 are the ones the rest of the documentation reasons
about, and option A compresses the `DEGRADED` → `FAULT` ladder that `T7`/`T8`/`T9` depend on. B moves
the *fault severity* rather than the *safety limit*, which is the right knob: a simulator that cannot
produce a dangerous machine is a limitation of the fault model, not evidence that 25 mm/s is the
wrong limit.

The over-temperature margin (0.46 °C, already recorded in `EQUIPMENT_SIMULATOR.md` §11) should be
resolved by the same decision rather than separately.

**Status: RESOLVED — option B, 2026-09-15.** Applied in `EQUIPMENT_MODEL_AND_STATE.md` §1.3 and
§2.2: vibration coupling 6.0 → 12.0, `OVERLOAD` ×1.6 → ×2.8, `c` cap 0.9 → 0.95. All three sensed
protective conditions are now reachable from a documented fault profile, so `AC-020` is proven by
physics rather than by injection. The thresholds in §3.4 were not touched.

---

# OD-003 — RESOLVED 2026-09-16 — Modbus TCP cannot enforce a read-only gateway the way the contract claims

> **Decision: option A.** Two Modbus listeners — read-only `5020` for the gateway, write-capable
> `5021` for the Control Service. Applied in `OT_PROTOCOL_MAPPING.md` §2.1.

`OT_PROTOCOL_MAPPING.md` obligation 9 and `EQUIPMENT_SIMULATOR.md` §16 both state that the gateway's
OT session is provisioned **without write permission**, so that "the gateway is read-only toward
equipment" is enforced by the server rather than trusted of the client. This is the protocol-level
expression of **FR-035**, which is a P0 safety requirement: the Control Service is the sole
application writer to equipment.

For OPC UA this is implementable — sessions, users and roles exist.

**For Modbus TCP it is not.** Modbus TCP has no authentication, no session identity, and no
per-client permission model. A TCP connection carries a unit id, a function code and an address;
there is no principal. There is nothing to provision and nothing for the server to check.

Found while challenging ADR-0020: the ADR repeated the claim from the contract without noticing that
one of the two protocols cannot satisfy it.

### Options

| | Option | Consequence |
|---|---|---|
| A | **Two listeners.** A read-only Modbus listener for the gateway that rejects every write function code with exception `0x01`, and a separate write-capable listener for the Control Service. | Genuinely server-enforced: the gateway's listener has no code path that writes, so the property holds without credentials. Costs a second port (the current map defines only `5020`), which is a contract change, and the separation is only as strong as the network policy that keeps the gateway off the write port. |
| B | **Network policy only.** One listener; a Kubernetes NetworkPolicy or firewall rule permits the write function codes only from the Control Service's address. | No contract change. But enforcement moves out of the application entirely, so it cannot be proven by an acceptance test in this repository, and `AC-039` would have to be reworded. |
| C | **Document the exception.** State plainly that Modbus-side read-only behaviour is *trusted of the gateway*, not enforced, and that OPC UA is the protocol where FR-035 is enforced at the OT boundary. | Honest and free, but it leaves a P0 safety requirement unenforced on one of the two protocols, which is exactly the class of gap the v0.2 review existed to remove. |

**Recommendation: A.** It is the only option where the property is true inside the software, which
means it is the only one an acceptance test can prove. B and C both move the guarantee somewhere this
repository cannot demonstrate it, and the project's own standard is that an unprovable claim is an
assertion.

A second port is a real cost and a real contract change, which is why this is a decision rather than
an implementation detail.

**Status: RESOLVED — option A, 2026-09-16.** `OT_PROTOCOL_MAPPING.md` §2.1 now defines a read-only
listener on `5020` and a write-capable listener on `5021`, and §3 obligation 9 states the mechanism
per protocol instead of asserting one that Modbus cannot provide.

The residual risk is recorded rather than hidden: **the separation is only as strong as the network
policy that keeps the gateway off `5021`**. A gateway misconfigured to `5021` can write, and option A
does not prevent that.

What option A buys over option B is something else, and it is worth stating precisely. With the
correct port, **no defect or compromise in the gateway can produce a write at all** — the connection
it holds carries no write function codes. Option B would have to filter by Modbus *function code* to
achieve the same thing, which an IP-and-port network policy cannot do without deep packet
inspection. So A moves the failure mode from "any gateway bug can write" to "only a wrong port can
write", and a port is a reviewable configuration value.

---

# OD-004 — RESOLVED 2026-09-17 — OPC UA has no restart signal, and two documents disagree about what one means

> **Decision: both sub-decisions approved.** Found while implementing the Phase 2 OPC UA client;
> challenged by Codex before any option was drafted, at the product owner's instruction.

## The problem

`EDGE_GATEWAY.md` §14 requires the gateway to tell an equipment **restart** from a telemetry **gap**
using `sourceEpochMs`. The Modbus register map carries that value at `+20`. The OPC UA address space
(§1.2) had ten nodes and **none of them was a restart signal**, so the rule was evaluable on one
protocol and not the other. After a restart the two paths would have disagreed on `SEQUENCE_GAP`,
which `AC-021` forbids, and `AC-023` could not be honestly proven on OPC UA either way.

Challenging it surfaced a second defect nobody had noticed: **`TIME_AND_DATA_QUALITY.md` and
`EDGE_GATEWAY.md` said opposite things** about an epoch reset. One raised `SEQUENCE_GAP`; the other
suppressed it. The Phase 1 implementation had followed `EDGE_GATEWAY.md` without either document
being read against the other.

## Decision 1 — protocol parity

Add a per-equipment read-only **`SourceEpochMs`** (`UInt32`) node to the OPC UA address space,
carrying the same value as Modbus register `+20`. `OT_PROTOCOL_MAPPING.md` §1.2 now lists **eleven**
nodes.

Two OPC UA-native alternatives were rejected on evidence rather than taste:

| | Why not |
|---|---|
| `SourceTimestamp` | already contracted as `eventTimeUtc`; it is wall-clock value timing, continues monotonically across a restart, and does not encode milliseconds since equipment start |
| `ServerStatus.StartTime` | standard, but scoped to the **server process**. One simulator server hosts many equipment, so it cannot witness one machine restarting — and it changes on a server restart no equipment noticed |

## Decision 2 — epoch reset semantics

`EDGE_GATEWAY.md` is right and **`TIME_AND_DATA_QUALITY.md` was wrong**. A `sequence` reset paired
with a `sourceEpochMs` reset is a restart or the 2^32 ms wrap, **not** `SEQUENCE_GAP`. Either signal
resetting alone **is** a gap.

The safety argument, which is the part worth keeping: **a restart is a discontinuity, but it is not
the same event as a dropped sample inside one sequence epoch**, and `AC-023` is about the latter.
The paired-signal requirement is what stops the restart rule from hiding real loss — suppression
fires only when both signals agree, so a single-signal reset is always reported.

The rejected reading would have made the 49.7-day wrap a data-loss incident, made
`telemetry_sequence_gaps_total` untrustworthy, and pushed canonical quality to `UNCERTAIN` for a
condition the contract says is distinguishable.

**The Phase 1 simulator is unaffected** — resetting `sequence` and `sourceEpochMs` together on wrap
was already correct. Only the gateway's interpretation was in question.

## Applied in

`OT_PROTOCOL_MAPPING.md` §1.2 · `EDGE_GATEWAY.md` §14 · `TIME_AND_DATA_QUALITY.md` §4 ·
`ACCEPTANCE_CRITERIA.md` AC-023 · `TEST_SPECIFICATIONS.md` OT-004 · the simulator address space and
the gateway client.

No new quality flag was added. The flag vocabulary is schema-closed, so making a restart visible in
the event stream would be a schema-versioning decision in its own right, and nothing has asked for
it yet.

---

# OD-005 — RESOLVED 2026-09-21 — polling at the producer's cadence cannot be lossless, and the criteria demanded that it be

> **Decision: amend the acceptance criteria.** Approved after the §1.3 subscription was implemented
> and the two paths were measured side by side, at the product owner's instruction — the decision
> was explicitly deferred until there was a number.

## How it surfaced

`LOAD-001` was built and run for the first time. It failed: 20 equipment, real sockets, 6.5 % of
samples reported as `SEQUENCE_GAP` with nothing inducing loss.

Two things came out of investigating it. One was a genuine defect — the poll loop slept for
"however much of the period is left", and because a timer only ever wakes **late**, the error
accumulated and the gateway settled into polling slightly slower than the equipment produced.
Anchoring on absolute deadlines took coverage from 89 % to 97.4 %. The rest was structural.

## What was measured

Claude's argument was that Modbus polling is lossy while OPC UA's subscription is not. Codex
rejected the second half: **the OPC UA client was not subscribing at all**. It used a batch read
through the same polling abstraction, so §1.3 — publishing 100 ms, sampling 50 ms, queue 10,
discard-oldest — was entirely unimplemented, and the comparison had never been made.

The subscription was implemented and both paths were run against the same simulator, same 60 s,
same machine:

| | Modbus, polled | OPC UA, §1.3 subscription |
|---|---|---|
| produced | 601 | 601 |
| distinct samples received | **439** | **599** |
| coverage | **73.0 %** | **99.7 %** |
| sequence gaps | **161** | **0** |
| duplicate reads | 162 | — |

161 gaps and 162 duplicates against 601 produced is textbook equal-cadence phase drift: the poller
alternates between re-reading a sample it already has and missing one it never saw. Codex confirmed
no further defect is needed to explain it.

**Two measurement bugs had to be fixed first, and the first run said the opposite.** Samples were
assembled by grouping all ten monitored items on a shared source timestamp, which never completed
because a monitored item reports **on change** and `State` sits at `RUNNING` for the whole run — the
subscribed path scored 0.5 %. And coverage counted *records received* rather than *distinct
sequences*, so duplicate reads inflated the polled path to 99.8 %. Reported as they first came out,
those numbers would have justified exactly the wrong decision.

## Decision

`AC-023` stays a **detection** criterion and now says what each protocol can achieve:

- **OPC UA subscription** — zero gaps after start-up warm-up. The server queues, so a missed
  publishing cycle is recovered.
- **Modbus latest-register polling** — no **silent** loss. Not no loss: the register image holds only
  the newest value, and no polling discipline recovers what was overwritten.

`LOAD-001` drops "no dropped telemetry, no gaps" and instead **reports** records, distinct
sequences, duplicates, coverage, gaps, read failures, overruns and buffer drops, asserting per
protocol. Coverage is measured, not thresholded — setting a number before running on a
representative deployment profile would be inventing a target, and a laptop is not that profile.

Rejected: giving the simulator a sample queue (it stops being ordinary Modbus register polling) and
phase-locking the gateway to simulator ticks (it proves a synchronised lab, not an OT design).

## What this does not prove

Coverage was measured; **atomicity was not**. The subscription assembles a sample on the
`SequenceNo` notification from the latest known value of every other node, and callback ordering
could pair a marker from tick N with a measurement from N-1. Codex flagged it, and
`OpcUaSampleCoherenceTests` now checks that changing nodes carry the marker's own source epoch,
leaving stable nodes such as `State` as latest-known.

**Superseded 2026-09-21 by the Phase 2 verification (COD-P2-001).** The per-cycle k-th-value
pairing that replaced it assumed every item queued the same number of values per cycle, which OPC
UA does not guarantee. The subscription now uses the `StatusValueTimestamp` trigger, so every node
reports on every step, and assembles a sample from exactly the ten values that share the marker's
source timestamp (`OT_PROTOCOL_MAPPING.md` §1.3). Nothing is inferred about a silent node any more.

The Modbus half of this decision also needs a caveat. At **one** equipment the loss is the phase
drift described above. At **twenty**, a second cause was found — see OD-006 — and the 20-equipment
Modbus numbers taken before it is resolved measure that defect as much as the protocol.

---

# OD-006 — RESOLVED 2026-09-21 — option B — the NModbus client blocks a thread-pool thread per read, and twenty loops starve the pool

> **Decision: B**, by the product owner, after Claude and Codex both recommended it. The gateway's
> Modbus client is an in-repository async FC04 reader; NModbus stays on the simulator's server.
> `ADR-0020` is amended accordingly.
>
> Raised 2026-09-21 by the Phase 2 verification. **Major dependency** and **concurrency** are both
> on the mandatory Codex participation list (`DUAL_AGENT_PROTOCOL.md` §2), and the options below
> change what ADR-0020 selected, so this is the product owner's decision.

## How it surfaced

Codex required `LOAD-001` to assert per protocol (COD-P2-007). Run for 20 s with 20 equipment,
Modbus alone, the "zero read failures on a healthy run" assertion failed: 12 of 3 950 reads hit the
250 ms response timeout (§2.7), with 20 loop overruns, 325 gaps and 90 % coverage. The new
real-path AC-001 test fails intermittently for the same reason when the suite runs in parallel.

## Cause — confirmed, not inferred

NModbus 3.0.83's `ReadInputRegistersAsync` is not asynchronous. Decompiled:

```csharp
private Task<ushort[]> PerformReadRegistersAsync(ReadHoldingInputRegistersRequest request)
{
    return Task.Factory.StartNew(() => PerformReadRegisters(request));   // a blocking socket read
}
```

Every read parks a thread-pool thread in a synchronous receive. Twenty loops at 10 Hz outrun the
pool's injection rate, the timers that drive the loops queue behind the blocked threads, and reads
time out. The simulator's side (`ModbusMasterTcpConnection.HandleRequestAsync`) is genuinely async
and is not involved.

The same 20 s run with the pool's minimum raised to 64 threads:

| | default pool | min 64 threads |
|---|---|---|
| read failures | **12** | **0** |
| loop overruns | 20 | 0 |
| sequence gaps | 325 | 38 |
| coverage | 90.0 % | 99.1 % |

A timeout is also worse than it looks: `WaitAsync` abandons the task, not the read. The blocked
thread stays parked on the socket until the loop reconnects and disposes it, and the response it was
waiting for would have been read as the answer to the **next** request had the loop not reconnected.

## Options

| | Option | Cost | Ceiling |
|---|---|---|---|
| **A** | `ThreadPool.SetMinThreads` sized to the Modbus loop count at gateway start | one line, no dependency change | one pool thread per in-flight read; at LOAD-002's 250 equipment the pool is sized for 250 blocked readers, and a slow endpoint holds each for the full timeout |
| **B** | Replace the NModbus **client** with a small, genuinely async FC04 reader (MBAP header, one request, one response, exception PDU → error); keep NModbus for the simulator's server | ~60 lines of protocol code; ADR-0020 amended to "server only" | the reader is hand-written, but `CrossProtocolTests` checks it against an independent implementation — NModbus serving, OPC UA typed values |
| **C** | One dedicated thread per Modbus connection, calling NModbus's synchronous API | a small per-connection worker | a thread per equipment by design; 250 threads at LOAD-002 |

**Recommendation: B.** It is the only option that removes the blocking rather than provisioning
for it, a cancelled read closes its own socket so no stale response can survive, and the gateway
loses a dependency instead of gaining a workaround. A is acceptable as an interim if B is deferred.

**Codex, round 2 (COD-P2-R2-002):** confirmed the cause independently by decompiling NModbus, and
takes **B**: "Option A is acceptable only as an interim mitigation; Option C preserves the
thread-per-connection ceiling." Agreement between the agents is input, not the decision.

## After — measured, default thread pool

The same 20 s, 20-equipment run on the new reader, with the pool left at its default size:

| | NModbus, default pool | NModbus, min 64 threads | **async reader, default pool** |
|---|---|---|---|
| read failures | 12 | 0 | **0** |
| loop overruns | 20 | 0 | **0** |
| sequence gaps | 325 | 38 | **0** |
| coverage | 90.0 % | 99.1 % | **100.0 %** |

One run, on a laptop, is not a performance claim (NFR-005): coverage is phase-dependent (OD-005)
and this run happened to land well. What it does show is that the defect is gone without sizing
anything around it. The 30-minute `LOAD-001` run is what reports the figure.

## Before it was resolved

The Modbus AC-001 real-path test and the Modbus half of `LOAD-001` measured this defect, and were
left **failing when they failed** rather than relaxed: a test weakened to pass around a known defect
is the silent loss D-03 forbids, moved into the test suite. They are the tests that now have to
pass on the new reader, unchanged.

---

# OD-007 — RESOLVED 2026-09-28 — option B — `AC-003` cannot be proven in the phase that is asked to prove it

> Raised 2026-09-23 by the Phase 3 Definition-of-Ready check and confirmed by Codex
> (`reviews/phase-3/`, `P3-DOR-001`, `P3-DOR-002`). Product owner's decision: it changes what a
> phase is allowed to close on.

## The problem

`IMPLEMENTATION_PLAN.md` lists **AC-003** in Phase 3's proof list. AC-003 requires two things:

1. a replayed Kafka range rebuilds **read models** identically, and
2. the **Safety Supervisor** issues no command as a result (F11).

PostgreSQL projections and read models are **Phase 4**. The Safety Supervisor is **Phase 6**. The
Control Service that would issue a command is **Phase 7**. Phase 3 can build the replay policy, the
consumer groups and the offset semantics that make both claims *possible*, and can prove none of it.

`FAIL-KAFKA-001` in `TEST_SPECIFICATIONS.md` repeats the same obligation, so the defect exists in two
places and fixing only the plan would leave the test specification demanding absent services.

This is the same failure mode as `AC-001` at Phase 1, which was moved to Phase 2 because canonical
telemetry needs a gateway. It was caught then; this one survived because AC-003's dependency is on
two services rather than one.

## Options

| | Option | Cost |
|---|---|---|
| **A** | Move `AC-003` wholesale to the last phase it depends on (Phase 6) | honest, but Phase 3 and Phase 4 then close with a replay mechanism nothing has checked, and a defect in it surfaces three phases later |
| **B** | **Split by the capability each half needs.** Phase 3 proves what replay *is* — a projector group can be re-driven over a known offset range, offsets are committed manually, the Supervisor's group is configured not to replay. Phase 4 proves read models rebuild identically. Phase 6 proves the Supervisor issues no command | AC traceability churn: one criterion becomes three, and `FAIL-KAFKA-001` splits with it |
| **C** | Pull the projector forward into Phase 3 | Phase 3 grows a database and a read model, and the phase dependency story ("Kafka first, consumers after") stops being true |

**Recommendation: B.** Each half is proven by the first phase that *can* prove it, which is the rule
`AC-001` already set. A leaves the longest gap between building a mechanism and testing it, and C
buys nothing except an earlier date.

## Decision — B, with the conditions Codex attached

Codex challenged the intended decision before it was recorded
(`reviews/phase-3/CODEX_OD_DECISION_CHALLENGE_raw.md`) and returned `SOUND_WITH_CONDITIONS`. All
three conditions are applied:

1. **One criterion still owns F11** (P0 in the challenge). Splitting a safety claim into three
   thirds that each pass separately is how the whole stops being anybody's job. `AC-003` therefore
   keeps the whole claim — *replay moves no equipment* — and moves to **Phase 6**, the first phase
   where the Supervisor's command intent can be observed. `FAILURE_MODEL.md`'s traceability row for
   "replay cannot move equipment" points at its test, not at the mechanics tests.
2. **New numeric AC IDs, not `AC-003a/b/c`.** `ac_traceability.mjs` matches `AC-\d{3}`, so lettered
   parts are invisible to it: Phase 3 would appear to cover "AC-003" while the untested parts hid
   behind a checker that cannot see them.
3. **Phase 3's obligation is named, not gestured at** — see `AC-045`.

| Criterion | Phase | Claim |
|---|---|---|
| **`AC-045`** *(new)* | 3 | a replay-eligible group can be rewound over a known offset range and consumes exactly those records in key order; offsets commit only after successful processing; the Supervisor's group is non-replay-eligible by configuration |
| **`AC-046`** *(new)* | 4 | replaying a known range into projection groups rebuilds read models byte-identically |
| **`AC-003`** *(amended)* | 6 | during and after a replay the Safety Supervisor issues **no** command, and no equipment moves (F11) |

---

# OD-008 — RESOLVED 2026-09-28 — option B — the equipment-state stream has three holes that are really one decision

> Raised 2026-09-23 by the Phase 3 Definition-of-Ready check (`P3-DOR-003`, `P3-DOR-005`,
> `P3-DOR-006`). Two of the three are **P0**. They are written as one decision because deciding them
> separately is what produced the contradiction.

## The problem

**1. `stateSequence` has no restart semantics.** `EVENT_CONTRACTS.md` §2 and `DEC-007` assign it to
the gateway, per equipment. `KAFKA_TOPOLOGY_AND_SEMANTICS.md` §7 makes `(equipmentId, stateSequence)`
the **duplicate identity**. The gateway holds no durable state — by design; its buffer is in memory
— so after a restart the counter starts again, and a new record collides with an old one's identity.
A consumer doing what the contract tells it to do then **discards current state as a duplicate**.

**2. Nothing says when a state record is emitted.** The obvious reading is "on every observed
transition". That collides with the Safety Supervisor's **gate 10**, which fails on
`equipment state stale > 10 s` (`SAFETY_SUPERVISOR.md`). A machine that runs normally for an hour
has no transition in that hour, so its state is stale after ten seconds and the Supervisor falls
back — permanently, on healthy equipment. Transition-only emission cannot satisfy a freshness gate.

**3. The topic's retention deletes the state it is supposed to preserve.**
`factory.equipment-states.v1` is `compact+delete` with **7 d** retention, and the Supervisor "must
read it in full to learn current equipment state" (§5). Compaction keeps the last record per key;
`delete` removes segments older than the retention regardless. A machine stable for more than seven
days loses its only record, and a Supervisor starting afterwards learns nothing about it.

The three are one decision: the emission rule sets what freshness the gate can rely on, the
retention has to hold whatever the emission rule leaves as the last record, and the identity has to
survive a restart in between.

## Options

| | Option | Cost |
|---|---|---|
| **A** | Transition-only emission · gateway persists the counter across restarts · retention `compact` only | the gateway acquires **durable state**, which contradicts its design and adds a recovery path to specify and test; gate 10 still has no freshness source, so it needs a separate contract |
| **B** | **Transition emission plus a periodic refresh inside the gate's budget** · the record carries a **gateway epoch** alongside `stateSequence`, and duplicate identity becomes `(equipmentId, gatewayEpoch, stateSequence)` · retention `compact` only, no `delete` | schema and identity change (one field, one contract line); a steady 20-machine deployment adds a few records a second, which is negligible next to 200 telemetry events a second |
| **C** | Emit a state record with every telemetry sample | 10 Hz × 20 machines on a state topic, for information the telemetry record already carries; compaction churn for no gain |

**Recommendation: B**, and the epoch half is not a new idea — it is exactly what `OD-004` decided for
telemetry. There, `sequence` alone could not distinguish a restart from data loss, so `sourceEpochMs`
was added and the **pair** carries the meaning. The same shape applies here: a counter that resets is
only safe when something else says it reset.

## Decision — B, with every number fixed

Codex returned `SOUND_WITH_CONDITIONS` and required four things to be specified rather than left to
the implementer. They are specified here and applied to the contracts.

**1. Refresh interval — 2 s.** The constraint, not the number, is the contract:

```
stateRefreshInterval + worst-case produce-to-consume latency + clockSkewBudget  <  10 s
```

`clockSkewBudget` is **±250 ms** (`TIME_AND_DATA_QUALITY.md`), gate 10's staleness limit is **10 s**
(`SAFETY_SUPERVISOR.md`). At 2 s the budget left for end-to-end latency is 7.75 s, which is ample
against the 1 200 ms P95 in `L-07`. Volume is `equipment / interval`: **10 records/s** at the
20-machine profile and **125 records/s** at `LOAD-002`'s 250 — against 2 500 telemetry events/s at
the same scale, which is where the real load is. The interval must be re-checked at `LOAD-002`,
because a gate that fails under load is a gate that fails.

**2. `gatewayEpoch` — the paired signal.** Integer milliseconds since the Unix epoch, taken **once
at gateway process start**. It changes exactly when `stateSequence` can restart, and it is **not an
ordering signal**: two gateways' epochs are not comparable, and a consumer must never sort by it.
Duplicate identity becomes `(equipmentId, gatewayEpoch, stateSequence)`. The Kafka **key stays
`equipmentId`**, so compaction is unaffected — the epoch changes deduplication, not retention.
The schema is `additionalProperties: false`, so this is a contract change, not prose: schema,
example, topology and the `EVENT_CONTRACTS.md` scope table all move together (`ADR-0017`).

**3. Retention — `compact` only, and a tombstone rule.** Removing `delete` fixes the
current-state-ages-out defect and creates the cost Codex named: decommissioned equipment would
never disappear. The **gateway** — the topic's only producer — writes a **tombstone** (a null value
under the equipment's key) when an equipment leaves its configured inventory. Without that rule,
"read the topic in full to learn current state" eventually returns machines that no longer exist.

**4. A refresh is an observation, not a replayed transition.** A refresh record carries a **fresh**
`occurredAtUtc` and `ingestTimeUtc`, and omits `previousState` and `transitionId`, which describe a
transition that is not happening. Gate 10 measures the **age of the observation** — wall clock minus
`occurredAtUtc`, allowing the skew budget — not the age of the last transition. Measuring transition
age is precisely the reading that makes a healthy, stable machine look stale.

---

# OD-009 — RESOLVED 2026-09-28 — option A — `AC-027` describes consumer behaviour, and Phase 3 has no consumers

> Raised 2026-09-23 by the Phase 3 Definition-of-Ready check (`P3-DOR-008`).

## The problem

`AC-027` requires a schema-invalid record to reach the DLQ **on the first attempt** with all required
headers. That is behaviour of an application consumer: the broker does not validate JSON Schema, and
Phase 3's deliverables are topics, producers, offset semantics and redrive tooling. The first real
consumers arrive with the projector in Phase 4.

Phase 3 can create the DLQ topics and prove they exist. It cannot prove anything routes to them.

## Options

| | Option | Cost |
|---|---|---|
| **A** | **Phase 3 owns a shared consume-validate-DLQ component**, proven against a test consumer, and every later consumer is built on it | scope added to Phase 3 now, and the component has to be right before any consumer depends on it |
| **B** | Each consumer implements DLQ behaviour; `AC-027` moves to Phase 4 with the first one | the rule gets re-implemented per consumer, which is how two consumers end up disagreeing about what "invalid" means |
| **C** | Introduce a schema registry and validate at the broker edge | a new dependency and its own ADR, plus a second source of truth for schemas that `ADR-0017` says are authoritative in this repository |

**Recommendation: A.** The poison-message rule and the DLQ header set are already specified once;
implementing them once matches that. B is cheaper this week and is the option most likely to produce
a silent divergence later, which is the failure this repository keeps finding.

## Decision — A, bounded so it cannot become a framework

Codex returned `SOUND_WITH_CONDITIONS`, with the objection worth keeping: a shared component built
before a single consumer exists is the shape of speculative abstraction, which this repository
otherwise refuses. It is justified **only** under these limits:

- The component is the **production consume path**, not a test helper: every later consumer —
  projector, feature builder, Supervisor — is built on it, and that adoption is a condition of
  those phases, not an aspiration.
- Its scope is exactly the rules already written in `KAFKA_TOPOLOGY_AND_SEMANTICS.md` §9 and
  nothing else: validate against the authoritative JSON Schema · schema-invalid → DLQ on the
  **first** attempt, no retry · required DLQ headers · commit the source offset only **after** the
  DLQ produce succeeds · no automatic redrive.
- The Supervisor's exception — reject first, DLQ second, never "skip and continue" — stays Phase 6
  and is not built into the shared path.
- `AC-027` is proven against a consumer that runs **this** component on a real topic, and asserts
  first-attempt routing, the full header set, and that downstream processing did **not** run. A
  test-only path would demonstrate the behaviour rather than prove the criterion.

## Not part of this decision

The Safety Supervisor's exception — reject first, then DLQ, never "skip and continue"
(`KAFKA_TOPOLOGY_AND_SEMANTICS.md` §9) — stands whichever option is chosen, and is Phase 6 work.

---

# OD-010 — RESOLVED 2026-09-29 — what an equipment-state record may claim that the gateway cannot observe

> Raised 2026-09-29 while starting Phase 3 step 4, binding the gateway's egress to
> `factory.equipment-states.v1`. Two fields of `equipment-state.schema.json` ask the gateway for
> something the contracts never gave it a way to know. `CLAUDE.md`: an ambiguity is written down
> and decided, not guessed.

## 1. `activeConditions`

The schema has an `activeConditions` array — `VIBRATION_HIGH`, `TEMPERATURE_TRIP`, and so on — and
the documented example populates it. But **neither protocol carries the equipment's conditions.**
`OT_PROTOCOL_MAPPING.md` gives Modbus register `+22` (`statusBitmap`) exactly one meaningful bit,
fault-injection-active, with bits 1–15 reserved; the OPC UA address space has no conditions node.
The simulator knows its conditions; the gateway is never told them.

Nothing consumes the field today: the Safety Supervisor's gate 10 reads `state`, and
`aiEligible` is derived from it. So this is not a safety gap. It is a contract that describes data
no producer can honestly fill.

| | Option | Cost |
|---|---|---|
| **A** | **Omit it.** The field is optional; the gateway does not emit what it cannot observe. The schema's description and the example are corrected to say it is populated only once a protocol carries conditions | none now; the example currently implies the opposite, and must change |
| B | The gateway **derives** the conditions it can see from telemetry — `QUALITY_UNCERTAIN` from `quality.overall`, `VIBRATION_HIGH` above 12.0 mm/s, `TEMPERATURE_HIGH` above 95 °C | re-implements the equipment's thresholds in a second place, where they can drift; trip conditions cannot be derived at all, so the list would be silently partial |
| C | **Extend the protocol mapping** to carry a conditions bitmap — register `+22` bits 1–15, and an OPC UA node | a contract change on both protocols and the simulator; the right answer if a consumer ever needs the field |

**Recommendation: A.** A field that claims to list the equipment's conditions and silently lists
only the ones a gateway happened to be able to infer is worse than an absent field — the same
reasoning `ADR-0018` applies to measurements. C is the real fix when something needs it; nothing
does yet.

## 2. `transitionId` for a change that is not in the table

`transitionId` is `T1`–`T12` from `EQUIPMENT_MODEL_AND_STATE.md` §3.2, and nullable. An observer
sees changes the table does not name:

- connecting to a machine that is **already** `DEGRADED`, `FAULT` or `STOPPING` — the table has
  `CONNECTING → IDLE` (T2) and `CONNECTING → RUNNING` (T3) only;
- an intermediate state the observer never saw, when the equipment passed through it between two
  samples.

| | Option | Cost |
|---|---|---|
| **A** | **`null`**, defined to mean "an observed change that is not one documented transition" | a consumer must handle `null`; the schema already allows it |
| B | Pick the nearest documented transition | a fabricated fact — the same substitution `ADR-0018` forbids for values |
| C | Suppress the record until a documented transition is seen | the consumer's view of state stalls exactly when it is least certain |

**Recommendation: A.** It is the only option that does not invent information. The table gap for
connecting to a machine in a non-`IDLE`, non-`RUNNING` state is recorded here too: it is real, and
`null` is how an honest observer reports it.

## Codex challenge, 2026-09-29 — and what it changes

`reviews/phase-3/CODEX_OD-010_CHALLENGE_raw.md`. Codex agreed on part 1 and disagreed on part 2, and
found a third item. Each was checked against the documents before being written here.

**Part 1 — `activeConditions`: A holds, with two corrections.** "Nothing consumes the field" was too
broad: `factory.equipment-states.v1` is consumed by the Supervisor and the operations projector.
What is true is narrower — **no documented consumer reads `activeConditions`**. And A is not
complete without fixing the evidence that points the other way: the validated example populates the
field, which teaches an implementer to emit what cannot be known. So A now means: omit the field,
**remove it from the example**, and say in the schema description that it is absent until the
protocol mapping carries condition identity. C remains the real fix, and Codex adds that it is not
just the reserved Modbus bits — OPC UA has no matching node, so it needs a closed bitmap defined
for both protocols in one contract change.

**Part 2 — `transitionId`: the recommendation changes from A to FIX_TABLE.** `null` is honest for an
intermediate state the observer genuinely never saw. It is not honest as a patch over transitions
the model simply forgot. §3.2 has no row for a first observation of a machine that is already
`DEGRADED`, `FAULT` or `STOPPING`, and the `connect_timeout` return to `OFFLINE` exists only in T2's
timeout column, with no ID. Those are known state-machine facts, and a table missing them is a
model defect. Revised recommendation:

| ID | From | To | Trigger |
|---|---|---|---|
| T13 | `CONNECTING` | `DEGRADED` | first valid telemetry reports `DEGRADED` |
| T14 | `CONNECTING` | `FAULT` | first valid telemetry reports `FAULT` |
| T15 | `CONNECTING` | `STOPPING` | first valid telemetry reports `STOPPING` |
| T16 | `CONNECTING` | `OFFLINE` | `connect_timeout` 10 s with no valid telemetry |

— with the schema pattern widened to `T1`–`T16`, and `null` kept **only** for an observed change
that is not representable as one transition. None of T13–T16 makes a machine AI-eligible, and none
touches the forbidden transitions: `FAULT → RUNNING` and `OFFLINE → RUNNING` stay impossible.

**Part 3 — `aiEligible`, found by the challenge.** The schema describes it as carried explicitly "so
the Supervisor gate is a field read", yet leaves it out of `required`. A field the gate is meant to
read cannot be optional. Recommendation: **make it required** — a pre-producer amendment under
`EVENT_CONTRACTS.md` §5, the same exception already recorded for `gatewayEpoch`, and still open
because the topic has no producer yet.

## Decision — as revised by the challenge, confirmed by the product owner 2026-09-29

1. **`activeConditions`: omitted** by the gateway, removed from the validated example, and described
   in the schema as absent until the protocol mapping carries condition identity for **both**
   protocols (option C, when a consumer names a need for it).
2. **`transitionId`: the table is fixed.** T13–T16 are added to `EQUIPMENT_MODEL_AND_STATE.md` §3.2
   and the schema pattern widens to `T1`–`T16`. `null` is reserved for an observed change that is
   **not representable as one transition** — an intermediate state the observer never saw.
3. **`aiEligible`: required.** A pre-producer amendment under `EVENT_CONTRACTS.md` §5, recorded in
   that section's table beside `gatewayEpoch`.
