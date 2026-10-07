# Baseline Decisions — Resolved for v0.2

The following baseline choices are now normative unless changed through the documentation-first workflow.

1. **Equipment model**: variable-speed motor-driven conveyor/drive unit with bearing degradation model.
2. **Demo scale**: 20 equipment instances at 100 ms telemetry. Load profile is configurable up to 250 virtual equipment instances; any published throughput claim must be measured.
3. **Telemetry cadence**: raw telemetry every 100 ms per equipment.
4. **Feature/inference cadence**: maintain a 60-second feature window, produce 1-second aggregate features, run inference every 5 seconds per equipment.
5. **OOD baseline**: training-distribution z-score envelope + hard feature validity/range checks. More advanced OOD methods require evidence and an ADR.
6. **Operational persistence**: PostgreSQL stores the latest reading per equipment per second (amended 2026-09-30 by OD-015 — 1 s aggregate *features* are Phase 5's alone), predictions, safety decisions, command results, fault-injection history, and model-deployment metadata. Raw 100 ms telemetry is not duplicated into PostgreSQL in baseline; Kafka provides short-term replay retention.
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

---

# OD-011 — RESOLVED 2026-09-30 — option A — Phase 4 is asked to prove a chain and an audit trail whose producers are Phases 5–7

> Raised 2026-09-30 by the Phase 4 Definition-of-Ready check (`reviews/phase-4/`, `P4-DOR-001`,
> `P4-DOR-002`, both P0). Product owner's decision: it changes what a phase may close on. Same class
> as `OD-007`.

## The problem

Phase 4's proof list contains **AC-006** (the telemetry → prediction → decision → command chain for a
correlation ID, in one query, within 200 ms) and **AC-029** (audit records of decisions, outcomes
and mode transitions, retained and queryable). Only telemetry and equipment state have producers
today. Predictions are Phase 5, safety decisions Phase 6, control outcomes and control-mode
transitions Phase 7.

Two different claims are tangled in each criterion:

- a **store-and-query** claim — given records on the topics, the projector keeps them and the API
  returns them correctly. That is Phase 4's own behaviour, and a projector's input is the **contract**,
  not the producer: schema-valid records on a real topic test it honestly, as Phase 3 tested the DLQ
  component with records no production producer wrote (`OD-009`).
- a **the-system-links-up** claim — the real producers stamp the identifiers so that the chain
  exists at all. Only the phase that adds the last producer can prove that. Fixture records cannot:
  they would show that the fixture is linked, not that the producers are (`P4-DOR-002`).

## Options

| | Option | Cost |
|---|---|---|
| **A** | **Split as `OD-007` did.** Phase 4 proves store-and-query on schema-valid records through real topics, under **new** AC IDs. `AC-006` keeps the whole claim — a *live* chain from the real producers — and moves to **Phase 7**, the first phase with every producer. `AC-029` is a store-and-query claim and stays in Phase 4 | two new criteria and their specifications; the projector for predictions, decisions and outcomes is written before any producer and must be revisited if a producer disagrees with the schema |
| **B** | Phase 4 projects only the two topics with producers; every read model and route for later topics moves to the phase that produces them | no speculative projector code, but the Operations API grows in four phases, and AC-028/046's "every read model rebuilds" is re-proven each time |
| **C** | Keep AC-006 and AC-029 in Phase 4, proven on fixtures | cheapest; Codex's P0 — it demonstrates the fixture, not the system |

**Recommendation: A.** It is the rule `AC-001` and `OD-007` set: each claim is proven by the first
phase that *can* prove it, and a whole claim keeps one owner. B is defensible and cleaner about
speculative code; it is the better choice if the product owner weighs "no code before its producer"
over "one projector, built once".

## Decision — A, with Codex's conditions (2026-09-30)

Codex: `SOUND_WITH_CONDITIONS` (`reviews/phase-4/CODEX_OD_CHALLENGE_raw.md`). Applied:

1. **New criterion `AC-047`** (Phase 4): the projector stores schema-valid prediction, decision and
   outcome records from the real topics, and the trace route returns the chain in one query. It is
   worded so it cannot be read as proof that producers link up.
2. **`AC-006` keeps the live-chain claim and moves to Phase 7**, the first phase with every producer.
3. **`AC-029` stays in Phase 4**, with its scope stated: storage, retention and query of schema-valid
   records, not the existence of their producers. Its mode-transition part depends on `OD-019`.

---

# OD-012 — RESOLVED 2026-09-30 — option A — the Operations API contract requires values no component can supply yet, or ever reliably

> Raised 2026-09-30 while reading `contracts/openapi/operations-api-v1.yaml` for the Phase 4 DoR check.
> `P4-DOR-008` found one instance (`CorrelationTrace`); there are more, and the pattern outlives
> Phase 7.

## The problem

Fields marked **required and non-nullable**, with no source before a later phase:

| Schema | Field | Source | Phase |
|---|---|---|---|
| `EquipmentSummary` | `controlMode` | control-service outcomes | 7 |
| `EquipmentDetail` | `controlEpoch` | control-service | 7 |
| `PlatformHealth` | `inferenceAvailable`, `supervisorHealthy`, `controlServiceHealthy` | prediction service, Supervisor, Control Service | 5–7 |
| `CorrelationTrace` | `decision`, `command` | Supervisor, Control Service | 6–7 |

This is not only a phase-ordering problem. After Phase 7, an equipment that has never had a control
outcome still has no `controlMode`, and a trace whose prediction was rejected has no `command`. A
required non-nullable field forces the API to invent a value — the thing `ADR-0018` forbids for
measurements — or to fail validation, which breaks `AC-030`.

## Options

| | Option | Cost |
|---|---|---|
| **A** | **Nullable, with one stated meaning: "no record observed".** `null` never means "not implemented"; it means the projection holds no record for it, which is true in Phase 4 and stays true for a genuinely new equipment later. The contract states the meaning per field | the dashboard must render "unknown" everywhere, which is honest but means every consumer handles it |
| **B** | An explicit `UNKNOWN` enum member (`controlMode`) and `status` objects (`health: {state: UNKNOWN}`) | stronger typing, but `ControlMode` is a control-plane enum shared with the proto contracts, and a value that exists only in the read API invites it leaking into the control path |
| **C** | Serve a route only once every required field has a source | nothing invented, but `/equipment` — the main route — would not exist until Phase 7 |

**Recommendation: A**, applied as a contract amendment before any producer or consumer of the API
exists (the `EVENT_CONTRACTS.md` §5 pre-producer rule, applied to the OpenAPI file). B's leakage risk
is the kind this repository treats as a safety concern.

## Decision — A, with Codex's conditions (2026-09-30)

1. The fields become **nullable, keys stay required**, so a response's shape never changes:
   `EquipmentSummary.controlMode`, `EquipmentDetail.controlEpoch`, `PlatformHealth.inferenceAvailable`,
   `supervisorHealthy`, `controlServiceHealthy`, `CorrelationTrace.decision`, `CorrelationTrace.command`.
2. Each states the same meaning in the contract: **`null` = no record observed**. It never means
   "not implemented".
3. Examples and tests for an equipment with no control outcome and a trace with no command are part
   of the Phase 4 test specifications (`P4-DOR-010`).

**Amended 2026-10-01** by the read-model mapping challenge (`reviews/phase-4/CODEX_API_MAPPING_raw.md`,
`P4-M-003`): `CorrelationTrace.decision` and `.command` became the arrays `decisions` and `commands`,
because nothing limits a correlation ID to one of each; an empty array carries this decision's
meaning. The same rule reaches `EquipmentSummary.qualityOverall` and `/models`'
`watermarkAgeSeconds`. It does **not** reach `PlatformHealth.fallbackRatePct`, which is omitted when
unmeasurable instead: an unavailable store is not "no record observed" (`P4-M-001`).

---

# OD-013 — RESOLVED 2026-09-30 — option A — the trace's telemetry link cannot be joined as the contract describes it

> Raised 2026-09-30 while reading the causation chain for the Phase 4 DoR check. Not in Codex's
> list; it sits under `AC-006`.

## The problem

`EVENT_CONTRACTS.md` §2 says `correlationId` is **constant across the whole chain**, starting at
telemetry. It cannot be:

1. **Every telemetry record gets its own correlation ID.** `TelemetryNormaliser.cs` assigns a new
   one per record (`CorrelationId = _newId()`), which is correct — a telemetry record is the start
   of nothing in particular.
2. **A prediction derives from a 60 s window** — about 600 records per equipment, each with its own
   correlation ID. One prediction cannot carry all of them.
3. **Raw telemetry is not in PostgreSQL** (baseline #6) and lives **6 h** in Kafka. A "single query"
   against the operational store cannot return raw telemetry records at all.

So `CorrelationTrace.telemetry` — "an array of telemetry objects" — has no source a single query
can reach, and the join the contract implies does not exist.

## Options

| | Option | Cost |
|---|---|---|
| **A** | **The chain starts at the prediction.** A prediction mints the correlation ID; its `causationId` is the window ID; the trace's telemetry link is the **window reference** (`windowId`, equipment, event-time range) plus the 1 s aggregates for that range from PostgreSQL. Raw telemetry correlation IDs stay per record and are not part of the chain | the trace shows aggregates, not raw records; `EVENT_CONTRACTS.md` §2 and `CorrelationTrace` change; the prediction contract (Phase 5) is amended before its producer exists |
| **B** | Keep raw telemetry in the trace by reading Kafka at query time | not a single query, fails after 6 h, and makes the API a Kafka consumer — `OPERATIONS_API.md` §3 excludes it |
| **C** | Store raw telemetry in PostgreSQL | reverses baseline #6: 250 equipment × 10 Hz into a relational store, for one route |

**Recommendation: A.** It states what the chain can actually be, and matches how the prediction is
already defined (`causationId` = `windowId`).

## Decision — A, with Codex's conditions (2026-09-30)

1. `EVENT_CONTRACTS.md` §2 and the prediction schema say the chain **starts at the prediction**.
2. `CorrelationTrace.telemetry[]` is replaced by `window`: the prediction's feature-window reference
   and that window's **per-second readings** (`OD-015`, option B) from the operational store.
3. A telemetry record keeps its own per-record correlation ID; it is not part of the chain.

---

# OD-014 — RESOLVED 2026-09-30 — option A — "byte-identical" is undefined, and a full rebuild would destroy audit history

> Raised 2026-09-30 by the Phase 4 DoR check (`P4-DOR-003`, P0), with a second defect found while
> checking retention.

## The problem

**1. No representation is named.** AC-028 says "byte-equivalent", AC-046 "byte-identical". Physical
table bytes are meaningless in PostgreSQL (MVCC, tuple layout, free space). An ingestion timestamp,
a sequence-generated key, a `now()` default, row order, or float text formatting would each make a
correct rebuild differ.

**2. Kafka does not hold what PostgreSQL keeps.** `OPERATIONAL_DATA.md` §9 defines a rebuild as
*truncate the projection tables and replay from Kafka*, and §7 marks every projection table
"rebuildable: yes". Retention says otherwise:

| Table | PostgreSQL keeps | Kafka keeps |
|---|---|---|
| `telemetry_aggregate_1s` | 30 d | 6 h |
| `prediction` | 30 d | 24 h |
| `safety_decision`, `control_outcome` | 90 d | 7 d |
| `equipment_state_history` | 1 y | compacted — latest per equipment only |

A full rebuild as written would **delete up to 83 days of safety-decision audit records** — the
records `AC-029` requires to be retained. After the Kafka retention window, PostgreSQL is the only
copy, so those rows are not rebuildable; they are authoritative history.

## Options

| | Option | Cost |
|---|---|---|
| **A** | **Canonical dump, range-scoped rebuild.** "Byte-identical" means a canonical export — rows ordered by primary key, a fixed column list, canonical text per type — compares equal. Projection tables carry **no** non-deterministic column: keys come from the event's duplicate identity, times from the event. A rebuild replaces rows **only within the offset range it replays**, never truncating what Kafka no longer holds. §7 reads "rebuildable within topic retention" | the rebuild is a range operation, not a truncate; audit tables need a documented backup, as `control_state` already does |
| **B** | Compare API responses instead | misses every column the API does not expose, which is where a projector bug hides |
| **C** | Raise Kafka retention to match PostgreSQL | 90 d of decisions and 30 d of 10 Hz telemetry on a laptop broker, to keep a sentence true |

**Recommendation: A.** It defines the comparison on what the AC is about — the read model's
content — and removes a destructive operation instead of documenting it.

## Decision — A, with Codex's conditions (2026-09-30)

1. **Canonical dump** specified in `OPERATIONAL_DATA.md` §9a: column order, row order, text form
   per type. Projection tables have no non-deterministic column.
2. **Provenance** (`source_topic`, `source_partition`, `source_offset`) is stored on every projected
   row, so a rebuild can replace exactly the rows of the range it replays.
3. §7 reads **"rebuildable within topic retention"**, `equipment_state_history` is **not**
   rebuildable (its topic is compacted), and **no rebuild truncates a table**. Past retention a row
   is the only copy; audit tables are backed up like `control_state`.

---

# OD-015 — RESOLVED 2026-09-30 — option B — the 1 s aggregate has a bucket rule and nothing else

> Raised 2026-09-30 by the Phase 4 DoR check (`P4-DOR-004`, P1).

## The problem

`TIME_AND_DATA_QUALITY.md` §3 fixes the **bucket**: event time (`eventTimeUtc`), absolute second
boundaries. §8 fixes **null handling for feature windows**: nulls excluded, `validSampleRatio`
against an expected count derived from cadence. Nothing states, for `telemetry_aggregate_1s`:
which statistics per channel; how quality and flags combine across a bucket; when a bucket is
**final** (records arrive in per-partition order, but a gateway reconnect can deliver a late one);
what happens to a record for a bucket already written.

The second issue is duplication. Phase 5's feature builder also computes 1 s aggregates
(`PREDICTION_SERVICE.md` §10) in Python. Two implementations of the same aggregate, in two languages,
is how the dashboard and the model come to disagree about the same second.

## Options

| | Option | Cost |
|---|---|---|
| **A** | **One definition, two implementations, one conformance fixture.** Per channel: `count`, `validCount`, `min`, `max`, `mean`, `validSampleRatio` (expected 10 per second); worst `qualityOverall`, union of flags. A bucket is an idempotent upsert keyed `(equipmentId, bucketStartUtc)`, recomputed from its records, so a redelivered record is not counted twice. A bucket is **final** once a record for the same equipment arrives whose event time is **5 s** past the bucket's end; a record for a final bucket is counted (`late_records_total`) and dropped. Lateness is measured in **event time in partition order**, not wall clock, so a replay makes the same decisions and the rebuild stays deterministic. A shared fixture of telemetry → expected aggregates is run by both the C# projector and Phase 5's Python code | the projector holds open buckets' records in memory (≤ 6 s × 10 Hz per equipment); a late record is lost from the aggregate, visibly, rather than merged non-idempotently |
| **B** | Store the latest record per second, no statistics | simplest, rebuild-trivial; `measurements` becomes "a reading" rather than "an aggregate", and baseline #6's "1-second aggregates" is amended |
| **C** | The operations projector consumes Phase 5's aggregates instead of computing its own | one implementation, but the operational store then depends on the AI pipeline, which `OPERATIONAL_DATA.md` §4 classifies as control-independent for a reason |

**Recommendation: A.** B is honest and cheap if the dashboard never needs more than "latest"; the
product owner should pick B if that is the case, since it removes the late-record question entirely.

## Decision — B, by the product owner (2026-09-30)

The product owner chose **B** over the recommended A: the dashboard needs the latest reading, and
B removes the late-record and open-bucket questions (Codex's conditions on A) entirely.

- `telemetry_aggregate_1s` becomes **`telemetry_reading_1s`**: the latest reading per equipment per
  second, keyed `(equipment_id, second_utc)`, `second_utc` = `eventTimeUtc` truncated to the second.
- The stored reading is the one with the greatest `(eventTimeUtc, sequence)`; an upsert replaces it
  only with a greater one. The result depends on which records exist, not on their order, so a
  replay is deterministic and nothing is held in memory across a restart.
- No statistics are computed in the operational store. 1 s aggregate **features** belong to Phase 5's
  feature builder alone, so there is one implementation, not two.
- Baseline #6 is amended to say so.

---

# OD-016 — RESOLVED 2026-09-30 — option A — who creates the Control Service's tables

> Raised 2026-09-30 by the Phase 4 DoR check (`P4-DOR-005`, P1).

## The problem

`OPERATIONAL_DATA.md` §7 lists `control_state` and `command_idempotency` as **control-service–owned
and authoritative**; §9 says migrations are applied "by a single owner service"; `OPERATIONS_API.md`
hosts the projectors and migrations are placed under `src/dotnet/OperationsService/Migrations` (§6).
Read together: the Operations service would own the schema of the one set of tables it must never
write, before their writer exists.

## Options

| | Option | Cost |
|---|---|---|
| **A** | **One database, one schema per owner, each owner migrates its own.** Phase 4 creates the `operations` schema only. `control` is created by the control-service's migrations in Phase 7. Least-privilege roles (§16) follow the schema boundary | two migration histories to keep ordered at deploy; §6 and §9 are amended |
| **B** | Phase 4 pre-creates the control tables from today's documents | the schema is written before the service that defines its needs, and the owner of a control-critical table becomes the read side |

**Recommendation: A.**

## Decision — A, with Codex's conditions (2026-09-30)

1. `OPERATIONAL_DATA.md` §6 and §9: migrations are **per owner, per PostgreSQL schema**.
2. Phase 4 creates the **`operations`** schema only.
3. The **`control`** schema and its migrations belong to control-service, in Phase 7.

---

# OD-017 — RESOLVED 2026-09-30 — option A — the first HTTP hop names "JWT" and nothing that issues or verifies one

> Raised 2026-09-30 by the Phase 4 DoR check (`P4-DOR-006`, P1).

## The problem

The OpenAPI contract declares bearer JWT with `viewer` and `operator` roles. It names no issuer,
signing algorithm, key distribution, audience, role-claim name, clock-skew allowance, or how a
local or demo run obtains a token. `DEFINITION_OF_READY.md` §8 requires the mechanism be named.
The rest of the system authenticates by mTLS (`ADR-0016`), so there is no precedent to inherit.

## Options

| | Option | Cost |
|---|---|---|
| **A** | **Asymmetric tokens, verifier holds only public keys.** ES256; the API validates `iss`, `aud`, `exp` (60 s skew) against a configured key set; roles in a `roles` claim. Local and demo tokens are minted by a repository script with a key pair generated on the developer's machine and never committed. Production-like takes the same validation with keys from its environment | a script and a key-generation step; no real login |
| **B** | Symmetric HS256 with a shared secret | simpler, but anything that can verify can also mint — the API could issue itself `operator` |
| **C** | An identity provider container (e.g. Keycloak) in every profile | real login flow, and a major dependency with its own ADR for a read-only API |

**Recommendation: A.** It names every value, keeps minting out of the verifier, and leaves C
possible later without changing what the API checks.

## Decision — A, with Codex's conditions (2026-09-30)

1. Recorded in `OPERATIONS_API.md` §16 and `SECURITY_BOUNDARIES.md`: ES256; `iss`
   `mair-local-issuer` locally, the environment's issuer otherwise; `aud` `mair-operations-api`;
   `exp` required, 60 s skew; roles in `roles`; `sub` is the rate-limit subject; public keys from
   configured key files.
2. It is a trust-boundary choice, so it is recorded as an **ADR** before any code, with the other
   Phase 4 dependency ADRs (`P4-DOR-009`).
3. No build or image contains a private key, and no route, flag or profile skips validation.

---

# OD-018 — RESOLVED 2026-09-30 — option the table — the numbers Phase 4 would otherwise invent

> Raised 2026-09-30 by the Phase 4 DoR check (`P4-DOR-007`, P1). One decision because each value is
> small and they interact through staleness.

| Value | Where | Proposed | Why |
|---|---|---|---|
| Readiness gate | `OPERATIONS_API.md` §9 | ready once every projector has been **caught up** (lag 0 at a poll) at least once since start | a lag *threshold* on a quiet topic is meaningless; "has caught up once" is observable |
| `X-Data-Staleness-Seconds` | OpenAPI header | seconds since the projector backing the route was last **caught up**; 0 while caught up | record age would show a quiet but current topic as stale; this separates "no news" from "behind" |
| Rate limit | §13 | 20 requests/s per token subject, burst 40, `429` with problem details | 50 dashboard clients polling at 1 Hz leave headroom; per-subject, not per-IP, since tokens are the identity |
| Retention partitions | `OPERATIONAL_DATA.md` §8, §15 | **daily** partitions for aggregates and predictions, monthly for the rest; a partition is dropped when its upper bound is older than retention | monthly partitions would keep 30-day aggregates for up to 61 days |
| Staleness when Kafka is down | `OPERATIONS_API.md` §11 | the same header; the body carries no separate field | one mechanism, not two that can disagree |

The product owner may accept the table as a whole or change individual values.

## Decision — the table, with Codex's conditions (2026-09-30)

1. **Caught up**: for every partition of every topic a projector consumes, records through the high
   watermark observed at a poll are written to PostgreSQL **and** their offsets committed.
2. `X-Data-Staleness-Seconds` is the **maximum** across the projectors backing the route.
3. The header is on **every projection-backed response**, not only equipment summary and detail.

---

# OD-019 — RESOLVED 2026-09-30 — option A — a mode transition is promised as an audited event the contract cannot carry

> Raised 2026-09-30 by Codex's challenge of OD-011–018 (`reviews/phase-4/CODEX_OD_CHALLENGE_raw.md`,
> `P4-ODC-001`, P1), confirmed against the repository. It blocks `AC-029`'s "mode transitions".

## The problem

`CONTROL_MODE_STATE_MACHINE.md` §8 says every mode change emits to `factory.control-outcomes.v1`
with `fromMode, toMode, transitionId (M1..M10), triggerReasonCode, controlEpoch, authenticatedSource,
decisionId?, occurredAtUtc, operatorId?, correlationId`. `control-outcome.schema.json` has
`resultingMode` and none of `fromMode`, `transitionId` or `triggerReasonCode`. A projector cannot
store a mode transition the contract cannot express, and `AC-029` cannot be proven on it.

## Options

| | Option | Cost |
|---|---|---|
| **A** | **Add the transition fields to `control-outcome`**, present when the outcome changes the mode and absent otherwise, as a pre-producer amendment (`EVENT_CONTRACTS.md` §5; the producer is Phase 7) | one schema carries two kinds of fact, distinguished by the fields' presence |
| **B** | A separate `factory.mode-transitions.v1` topic and schema | cleaner separation, but an eighth topic in a register Phase 3 froze and bootstrapped, and a second record for one event |

**Recommendation: A.** §8 already names `factory.control-outcomes.v1` as the destination; A makes the
schema say what the state machine already promised.

## Decision — A (2026-09-30), with a correction found while applying it

Checking the schema before amending it: `modeTransitionId` **already exists** and `reasonCode`
already carries the trigger, so of the three fields Codex named only **`fromMode`** is missing. The
check found a second drift instead: the schema's pattern allowed **M1–M12**, and the state machine
defines **M1–M10**.

- `fromMode` added, required whenever `modeTransitionId` is set; `null` only for M1, which has no
  prior mode.
- `modeTransitionId` narrowed to **M1–M10**.
- `CONTROL_MODE_STATE_MACHINE.md` §8 names the fields as the schema does.
- Both are pre-producer amendments (`EVENT_CONTRACTS.md` §5): control-outcome's producer is Phase 7.

---

# OD-020 — RESOLVED 2026-10-01 — option A — `factory.faults.v1` has a projection table and no contract

> Raised 2026-10-01 in Phase 4 step 2, while deriving the `operations` tables from the contracts.

## The problem

`OPERATIONAL_DATA.md` §7 lists `fault_injection` as a projection of `factory.faults.v1`, and
`KAFKA_TOPOLOGY_AND_SEMANTICS.md` gives the topic a producer (the simulator), a key and a duplicate
identity (`faultInjectionId`). **There is no `faults` schema** in `contracts/jsonschema/v1/` and no
example. The projector is built on the shared consume-validate-DLQ component (`OD-009`), which
validates every record against its contract: without one it has nothing to validate against, and a
table derived from no contract is an invention (`ADR-0017`). The producer side has the same gap —
the simulator's fault events are demo-profile work (Phase 11) and have never been emitted.

## Options

| | Option | Cost |
|---|---|---|
| **A** | **Defer `fault_injection` to the phase that produces fault events** (Phase 11, demo profile), which writes the schema first, then the producer and the projection | Phase 4's read model has six projected tables, not seven; `OPERATIONAL_DATA.md` §7 says when the seventh arrives |
| **B** | Write a faults schema now, from `FaultInjectionRequest` in the OpenAPI contract and the simulator's profiles, and project it in Phase 4 | a contract authored two phases before its producer, on a guess at what the producer will emit — the pattern `OD-011` avoided for the API by tying fields to an observed record |

**Recommendation: A.** No producer, no record and no consumer need it before Phase 11, and the
`OD-011` precedent is that a contract is written by the phase that can test it end to end.

## Decision — A, confirmed by the product owner (2026-10-01)

Codex: `SOUND` (`reviews/phase-4/CODEX_OD-020-021_CHALLENGE_raw.md`). Its three conditions restate
the decision and are kept: no `fault_injection` table or projector subscription in Phase 4;
`OPERATIONAL_DATA.md` §7 marks the table deferred; Phase 11 adds the schema, an example, the
producer, the projection and their tests together.

---

# OD-021 — RESOLVED 2026-10-01 — option A — the shared consumer dead-letters a tombstone, and the contract says tombstones are legitimate

> Raised 2026-10-01 in Phase 4 step 2. A Phase 3 defect that could not surface before: Phase 3 had
> no consumer of `factory.equipment-states.v1`.

## The problem

`OD-008` made `factory.equipment-states.v1` compact-only and gave equipment a way to leave: the
gateway writes a **tombstone** — a null value under the equipment's key — and Phase 3 implemented
it (`KafkaEquipmentStateSink`). `ContractConsumer.ProcessOneAsync` validates **every** record's bytes
against the schema; a null value is no JSON at all, so it is classed `Unparseable` and sent to the
DLQ on the first attempt. The operations projector — the topic's first consumer apart from the
Supervisor in Phase 6 — would therefore dead-letter every decommissioning and raise an alert for a
correct record.

Two things need deciding: **where** a tombstone is recognised, and **what the projection does**
with one.

## Options

| | Option | Cost |
|---|---|---|
| **A** | **The shared component treats a null value as a tombstone on a topic whose contract allows one**, passing it to a separate handler, and as `Unparseable` everywhere else. Only `factory.equipment-states.v1` (and later the compacted model-deployments topic, if its contract says so) allows one. The projection **keeps** the equipment's history — it is audit — and records the decommissioning, so "current equipment" excludes it | a widening of the bounded component `OD-009` allowed, by one rule that comes from a contract (`OD-008`), not from a consumer's convenience |
| **B** | Each consumer filters tombstones before the shared path | the rule re-implemented per consumer — exactly the divergence `OD-009` chose the shared component to prevent; the Supervisor (Phase 6) would need its own copy |
| **C** | The gateway stops writing tombstones | reverses `OD-008`: "read the topic in full" reports machines that no longer exist |

**Recommendation: A.** The tombstone is part of the contract, so the contract-enforcing component is
where it belongs. Deleting history on a tombstone would destroy audit records `AC-029` must keep.

## Decision — A, with Codex's conditions, confirmed by the product owner (2026-10-01)

Codex: `SOUND_WITH_CONDITIONS`. Each condition, and where it is now written:

1. **The allowance is topic metadata, not a name check.** The topic register gains a *Tombstones*
   column, `yes` only for `factory.equipment-states.v1` (`KAFKA_TOPOLOGY_AND_SEMANTICS.md` §2, §9),
   mirrored by `TopicSpec` in code. A consumer given a tombstone handler for a topic whose register entry does not allow
   tombstones fails at construction.
2. **Everywhere else a null value is still invalid**: `Unparseable`, first attempt, full header set
   (§9). Only on an allowing topic does the shared component pass it to the **tombstone handler**,
   with the handler-failure rules any handler has — three attempts, then the DLQ with
   `x-dlq-error-class: HandlerFailed` and every source header.
3. **The projection's shape** (`OPERATIONAL_DATA.md` §7, §10): history is **kept** — it is audit
   (`AC-029`). A tombstone is stored in its own table, `equipment_decommission` — shape in
   `OPERATIONAL_DATA.md` §10 — keyed by its provenance: a tombstone has no payload, so the record is
   its identity (topology §7, amended), one row per tombstone record. `decommissioned_at_utc` is the
   record's `CreateTime`, set by the gateway and stored in the log, so the same on every replay. An equipment is **current** when its newest state record
   is at a later offset than its newest tombstone; both share the equipment's key, so they share a
   partition and their offsets are comparable. An equipment that reappears is current again.
4. **The Supervisor uses the same component and the same rule** in Phase 6: a tombstone removes the
   equipment from its current-state view (`SAFETY_SUPERVISOR.md` startup step 3).
5. **Replay and DLQ**: a tombstone is processed in offset order and committed after its handler
   settles, like any record. Compaction removes a tombstone after the topic's `delete.retention.ms`,
   so like the history it is not rebuildable past that — and `equipment_decommission` is audit.
6. **Tests** (`TEST_SPECIFICATIONS.md`): `KAFKA-003` for the component — allowed tombstone handled,
   disallowed tombstone DLQ'd, tombstone handler failure DLQ'd after three attempts, construction
   refused for a non-allowing topic; `AUDIT-001` for the projection — history kept, current view
   excludes, reappearance restores, and a retained range rebuilt into a fresh store gives the same
   canonical dump. The Supervisor's adoption is tested in Phase 6.

Verified by Codex after it was written down (`reviews/phase-4/CODEX_OD-021_VERIFY_raw.md`): round 1
`REVISE` — the register had no tombstone column, §7's identity did not cover tombstones, the table had
no defined shape, the timestamp type was not pinned, and replay determinism was claimed but not
tested. All five applied. Round 2 found one more (P1): a 1 y retention on `equipment_decommission`
could resurrect an equipment whose last state row outlived the deleted decommission. The table is
now never deleted. Round 3: `ACCEPT`.

---

# OD-022 — RESOLVED 2026-10-02 — option A — a store outage would send the projector's records to the DLQ, and the store's own numbers are not numbers

> Raised 2026-10-01 at the start of Phase 4 step 5 (lifecycle), comparing `OPERATIONAL_DATA.md`
> §11–§12 with the shared consumer. Kafka semantics and failure recovery: Codex participation is
> mandatory.

## The problem

**1. Two documents prescribe different behaviour for the same failure.**
`OPERATIONAL_DATA.md` §11: when PostgreSQL is unavailable, *projections pause and resume from
committed offsets*. `KAFKA_TOPOLOGY_AND_SEMANTICS.md` §9, implemented by the shared consumer
(`OD-009`): a handler failure is retried **3** times with 1 s / 2 s backoff, then the record goes to
the DLQ and its offset is committed. The projector's handler fails on every record while the store
is down, so a three-second outage would dead-letter everything that arrived in it, commit past it,
and leave holes in the read models that only an operator redrive could fill. §9's rule is right for
a record the handler cannot process; it is wrong for a dependency that is briefly absent.

**2. Numbers that are not numbers.** §12 says "2–3 retries" — which? — and gives a circuit breaker
(5 consecutive failures, open 30 s) without saying what it protects. §11's "disk pressure" has no
threshold, and nothing says how often the retention job runs.

## Options for 1

| | Option | Cost |
|---|---|---|
| **A** | **The shared consumer distinguishes a dependency outage from a record failure.** A handler signals an outage by throwing a dedicated exception type; the consumer then does **not** count the attempt toward §9's three, does **not** commit, seeks back to the record, and retries it with capped backoff (1 s doubling to 30 s, ±20 % jitter) for as long as the outage lasts — polling throughout, so the group does not evict it. Every other exception keeps §9's rule. §9 gains one sentence; `OD-009`'s scope gains one rule | the bounded component grows by a second failure class; a handler that wrongly classifies a poison record as an outage would stall its partition — which is visible (lag, staleness) rather than silent |
| **B** | The projector's handler retries internally until the store is back | the shared rule stays untouched, but a handler that blocks for longer than `max.poll.interval.ms` (300 s) is evicted from the group and its partitions rebalance on every long outage; and the Supervisor in Phase 6 would need its own copy of the same loop |
| **C** | Keep §9 as is: dead-letter during an outage, redrive afterwards | contradicts §11; read models with holes after every outage; makes an operator part of normal recovery |

**Recommendation: A.** Only the shared consumer polls, so only it can wait without being evicted;
and "a dependency is down" is a fact about the world, not about the record, which is exactly what
§9's DLQ is not for.

## Proposed numbers for 2

| Value | Where | Proposed |
|---|---|---|
| Read retries | §12 | **none** in the API: one attempt within the 3 s query timeout, `503` on failure — the client retries; a server-side retry inside a 5 s request budget only delays the answer |
| Write retries | §12 | the projector's are option A's outage loop; no separate count |
| Circuit breaker | §12 | on the API's database calls: after **5** consecutive connection-class failures, answer `503` immediately for **30 s**, then let one request through to probe |
| Retention job | §11, §15 | runs at start and every **hour**; drops each partition whose upper bound is older than its table's retention; `equipment_decommission` never |
| Disk pressure | §11 | **dropped from Phase 4**: nothing measures it, and dropping data on a guessed threshold is worse than the retention job's fixed rule. Phase 8 (observability) owns a disk metric and its alert |


## Decision — A, with Codex's conditions, confirmed by the product owner (2026-10-02)

Codex: `SOUND_WITH_CONDITIONS` (`reviews/phase-4/CODEX_OD-022_CHALLENGE_raw.md`; the first attempt
stopped at a usage limit before a verdict). Its seven conditions and four findings, as applied:

**The outage path in the shared consumer** (`KAFKA_TOPOLOGY_AND_SEMANTICS.md` §9, `OD-009`'s scope):

1. A handler signals an outage by throwing **`DependencyUnavailableException`** — a distinct type,
   never inferred from "a database exception". Every other failure keeps §9: three attempts, then
   the DLQ.
2. **Ordering**: on an outage at offset N, the consumer **pauses every assigned partition** and seeks
   N's partition back to N. Nothing after N on that partition — and nothing on any other — is
   processed until N succeeds. It retries N itself, in hand.
3. **Polling continues** while paused, with each wait cut into polls of at most 500 ms, so
   `max.poll.interval.ms` (300 s) is never approached and rebalances are served.
4. **Rebalance**: if N's partition is no longer assigned, the consumer abandons N — no commit, no
   DLQ — and the next owner resumes from the last committed offset. A record a reassignment
   delivers during the wait is sought back, unprocessed, and its partition paused too.
5. **Caught up** is cleared the moment a record is consumed, before its handler runs (`P4-OD22-001`),
   so a stall can never show as current.
6. A misclassified poison record **stalls its partition visibly**: lag and staleness grow, the DLQ
   stays empty, and the consumer reports the outage — topic, partition, offset, exception type,
   since when, and attempts (`P4-OD22-003`).
7. **Backoff**: 1 s doubling to 30 s, ±20 % jitter. Used by the operations projector's writes only
   in Phase 4. The Safety Supervisor (Phase 6) decides its own adoption, without weakening
   reject-first-then-DLQ, and states its interaction with the prediction TTL and `seekToEnd`.

**"Unavailable"** (`P4-OD22-004`), the one classification the projector and the API share: a client
failure that is not a server-reported SQL error — connection refused or reset, network or query
timeout, pool exhausted — and these SQLSTATEs: class `08` (connection), `57P01`–`57P03` (shutdown,
cannot connect now), `53300` (too many connections), class `28` (authentication: a wrong password
stalls visibly rather than dead-lettering every record). Every other SQL error is a defect: the
API answers it with a `500` that the contract harness reports, and the projector's §9 path DLQs it.

**Numbers:**

| Value | Decision |
|---|---|
| Read retries | none: one attempt within the 3 s command timeout, `503` on an unavailable store |
| Write retries | the outage path above; nothing else |
| Circuit breaker | the API's database calls, **per instance**: 5 consecutive unavailable results open it; for 30 s every database route answers `503` at once; then **one** request is let through — success closes it, failure opens it for another 30 s; concurrent requests during that probe get `503`. Health does not use it |
| Retention job | at start and hourly — **per-table periods are OD-023** |
| Disk pressure | removed from Phase 4; Phase 8 owns a disk metric and its alert |

---

# OD-023 — RESOLVED 2026-10-02 — option A — the projector rewrites and deletes audit records the security boundary says nobody may change

> Raised 2026-10-02 while preparing the retention job (Phase 4 step 5). Data ownership, persistence
> and a security boundary: Codex participation is mandatory.

## The problem

`SECURITY_BOUNDARIES.md` (audit record): decisions and outcomes are stored in `control_outcome` (and
`safety_decision`), *append-only; no service holds UPDATE or DELETE permission on them*. What Phase 4
built does not honour that:

1. **Every projection write is `ON CONFLICT DO UPDATE`** — on the audit tables too. A redelivery
   rewrites the row (with identical values today, but nothing prevents otherwise).
2. **A range rebuild DELETEs** audit rows at or after the replayed offset (`ProjectionRebuild`, OD-014).
3. **One table, two retentions.** "90 days for outcomes, 1 year for mode transitions" — and since
   OD-019 a mode transition *is* a control outcome, in `control_outcome`, partitioned monthly. Dropping
   a 90-day-old partition would delete transitions that must live a year.
4. **`equipment_state_history` has no retention** anywhere; only the migration comment says a year.
5. **The least-privilege roles of `OPERATIONAL_DATA.md` §16** — projector write-only, API read-only —
   do not exist: every connection is one role. So "no service holds UPDATE or DELETE" is not enforced
   by anything.

## Options

| | Option | Cost |
|---|---|---|
| **A** | **Audit tables become insert-only, and retention follows the longest claim.** `safety_decision` and `control_outcome` are written `ON CONFLICT DO NOTHING`: a redelivery is a no-op, the first write in offset order wins, and a rebuild into an empty store replays the same order, so the canonical dump is unchanged. A range rebuild **does not delete** from audit tables — replaying the range re-inserts only what is missing. `control_outcome` is kept **1 y** whole (≥ 90 d for outcomes, = 1 y for transitions); `safety_decision` 90 d; `equipment_state_history` 1 y. Retention drops whole partitions — DDL by the migration owner's role, not row DELETE. Phase 4 creates the roles of §16 in its migration: the projector role may INSERT on audit tables and INSERT/UPDATE/DELETE only on the rebuildable ones; the API role may only SELECT | outcomes are kept 9 months longer than "90 days" required; roles add a second connection string and a migration that touches cluster-level objects |
| **B** | Amend `SECURITY_BOUNDARIES.md`: the projector may UPDATE and DELETE audit rows | the cheapest; the audit trail loses the property the security document gave it |
| **C** | Move mode transitions into their own table with a 1 y retention; outcomes 90 d | exact retentions, but a second record for one event — the duplication OD-019 chose to avoid — and a projection that splits a topic by content |

**Recommendation: A.** It makes the audit property true and enforced, keeps OD-014's determinism
(first-wins in offset order is as deterministic as last-wins), and resolves the retention conflict by
keeping more, never less.


## Decision — A, with Codex's conditions, confirmed by the product owner (2026-10-02)

Codex: `SOUND_WITH_CONDITIONS` (`reviews/phase-4/CODEX_OD-023_CHALLENGE_raw.md`). Applied:

1. **Audit tables are insert-only, first-wins in offset order.** `safety_decision` and
   `control_outcome` are written `ON CONFLICT DO NOTHING`: a later record with the same identity —
   at another offset, even with a different payload — leaves the first row as it is. A rebuild into
   an empty store replays the same order, so it keeps the same first rows. Tested with a conflicting
   duplicate.
2. **A range rebuild deletes only from tables a replay can restore** — `telemetry_reading_1s` and
   `prediction`, fed by delete-retention topics. It never deletes from the audit tables, nor from the
   tables fed by compacted topics (`equipment_state_history`, `equipment_decommission`,
   `model_deployment`, `authorization_watermark`): compaction keeps only the latest record per key, so
   a deleted history row would not come back. *(Found while applying this decision: the step 3 rebuild
   deleted from all of them.)* Replaying a range into those tables re-inserts only what is missing.
3. **Repairing an existing audit row is not a rebuild's job**: backup and restore, or an operator
   procedure.
4. **Retention, by whole partition**: `telemetry_reading_1s` and `prediction` 30 d; `safety_decision`
   90 d; `control_outcome` **1 y whole** — mode transitions live there (OD-019) and must last a year,
   so outcomes are kept a year too, which exceeds their 90 d; `equipment_state_history` 1 y;
   `equipment_decommission` never. `SECURITY_BOUNDARIES.md` is amended to say so.
5. **Roles for the `operations` schema only** (OD-016) — `control` is Phase 7's. Created by
   migration `0003` as group roles without login: `mair_ops_projector` (INSERT on the audit tables;
   INSERT, UPDATE, DELETE on the others), `mair_ops_reader` (SELECT), `mair_ops_retention` (EXECUTE
   on one function, nothing else). No runtime role holds UPDATE or DELETE on an audit table.
6. **Retention runs as `mair_ops_retention`**, through `operations.drop_expired_partitions(now)`,
   a `SECURITY DEFINER` function owned by the migration owner that drops only partitions of the five
   allow-listed tables whose upper bound is past that table's period. No row `DELETE`. The runtime
   connection never holds the owner role: each data source sets its role on connect, and migrations
   run on their own connection string.
7. **Tests**: `AUDIT-001` expects the periods above and proves, from the catalogue and by attempt,
   that no runtime role can UPDATE or DELETE an audit row.

---

# OD-024 — RESOLVED 2026-10-07 — option A — the one consume path is C#, and the first Python consumer arrives

> Raised 2026-10-07 by the Phase 5 Definition-of-Ready check (`P5-DOR-003`, P1).

## The problem

`OD-009` made one shared consume-validate-DLQ component the path every consumer is built on, and
named the feature builder among them, so that two consumers could never disagree about what
"invalid" means. That component is `ContractConsumer`, in C#. The prediction service — feature
builder and inference — is Python (`ADR-0003`). It cannot link the C# component.

## Options

| | Option | Cost |
|---|---|---|
| **A** | **A Python implementation of the same rules, held to the C# one by a shared conformance suite.** The rules are already written once (`KAFKA_TOPOLOGY_AND_SEMANTICS.md` §6, §9; OD-021, OD-022): validate against the authoritative JSON Schema, first-attempt DLQ for invalid and unparseable records, three handler attempts on §9's schedule, the seven DLQ headers, commit after settle, tombstones only where the register allows, the outage path. A fixture set of records with their expected outcome — verdict, DLQ headers, commit position — runs against **both** implementations in CI | two implementations of one rule set; the suite is what keeps them one |
| **B** | Build features in C# and publish them on a new topic the Python inference reads | a new topic, schema and producer; and training, also Python, would need the C# features too — the parity problem AC-032 is about, moved across languages |
| **C** | Amend OD-009: each language keeps its own consumer | the divergence OD-009 was decided to prevent |

**Recommendation: A.** The rules are the contract, and OD-009's real requirement is that every
consumer applies the same ones; a conformance suite proves that across two languages as well as it
would within one.

## Decision — A, with Codex's conditions

Codex: `SOUND_WITH_CONDITIONS` (`reviews/phase-5/CODEX_OD_CHALLENGE_raw.md`); confirmed by the product owner 2026-10-07.

1. The conformance suite is **mandatory in CI** for both implementations: unparseable, schema-invalid,
   handler failure on §9's schedule, tombstone allowed and refused, the outage path, the seven DLQ
   headers, and commit-after-settle.
2. Each fixture asserts the **commit position** and, for first-attempt DLQ, that the handler was
   **not** called.
3. The Python consumer reads tombstone allowance and DLQ naming from the topic register's data, not
   from topic names in code.
4. Nothing in it weakens the Supervisor's reject-first-then-DLQ rule (Phase 6).

---

# OD-025 — RESOLVED 2026-10-07 — option A — a dataset that is generated, not replayed, and labels that come with it

> Raised 2026-10-07 by the Phase 5 Definition-of-Ready check (`P5-DOR-004`, `P5-DOR-005`, P1). One
> decision, because where the data comes from decides where the labels come from.

## The problem

`AI_TRAINING_PIPELINE.md` builds datasets by **replaying a telemetry range** and labels them from
**fault-injection metadata**. Telemetry is kept **6 h** (`KAFKA_TOPOLOGY_AND_SEMANTICS.md` §2) while the
performance target names a **24 h** dataset, and a broker range is not something a later run can
reproduce (NFR-005). The labels' topic, `factory.faults.v1`, has no contract until Phase 11 (OD-020).
`EQUIPMENT_MODEL_AND_STATE.md` §2.1 makes `T_fail` the only RUL ground truth.

Two facts make another source possible: the simulator is **deterministic** — the same seed, profile
and `T_fail` give the same telemetry (Phase 1, `DeterminismTests`) — and the gateway's normaliser
runs **in-process**, as AC-001's 30-simulated-minute test already does.

## Options

| | Option | Cost |
|---|---|---|
| **A** | **Generate datasets.** A repository tool runs the simulator and the gateway's normaliser in-process — no protocols, no broker — over a configured fleet (seed, fault profile, `T_fail` per equipment, duration) and writes canonical telemetry records, each valid against `telemetry.schema.json`, plus a **manifest**: the configuration, the label metadata, the generator's git commit, and a SHA-256 of the records. Re-running the same manifest gives the same bytes. Labels come from the manifest, not from an event; the dataset's hash is what an MLflow run records | the dataset skips the protocol path — which is AC-021's to prove (cross-protocol agreement), not the dataset's; a C# tool beside Python training |
| **B** | Raise telemetry retention to cover the dataset, and author the faults contract now | 24 h of 10 Hz telemetry kept by the broker, and a contract written six phases before its producer — the pattern OD-020 declined |
| **C** | Archive a live run to object storage | an object store and a recorder to build and operate, and a recording is reproducible only as a file, not from configuration |

**Recommendation: A.** It is the only option that satisfies NFR-005 as written — generated by
repository configuration, with recorded metadata — and it gets labels from where they originate.

## Decision — A, with Codex's conditions

Codex: `SOUND_WITH_CONDITIONS` (`reviews/phase-5/CODEX_OD_CHALLENGE_raw.md`); confirmed by the product owner 2026-10-07.

1. The **dataset manifest** is a contracted artifact (`contracts/jsonschema/v1/dataset-manifest.schema.json`,
   written before the generator): fleet configuration, seed, fault profile and `T_fail` per
   equipment, duration, generator git commit, telemetry and feature schema versions, and the SHA-256
   of the canonical telemetry bytes.
2. Generated records pass the gateway's own normaliser and validate against `telemetry.schema.json`.
3. Labels are **manifest labels** — never presented as events from `factory.faults.v1`.
4. Every MLflow training run records the dataset hash and version (AC-008).

---

# OD-026 — RESOLVED 2026-10-07 — option A — what the prediction service may serve before anything authorizes a model

> Raised 2026-10-07 by the Phase 5 Definition-of-Ready check (`P5-DOR-006`, P1).

## The problem

The prediction service "loads authorized models" and stamps `deploymentStage`. Authorization is the
`mlops-publisher`'s compacted topic (ADR-0019), and the publisher is **Phase 9**. AC-008 asks that "a
**deployed** prediction is traceable to its MLflow model version and training run."

## Options

| | Option | Cost |
|---|---|---|
| **A** | **Phase 5 serves one model named in configuration, as `SHADOW` only.** The service loads `modelName` + version from the MLflow registry and stamps `deploymentStage = SHADOW` — the one stage that claims no authority; any other configured stage fails startup until Phase 9. AC-008 is proven as **traceability**: an emitted prediction's `model.name`, `version` and `runId` resolve in MLflow to a run that records the dataset hash, feature schema version and git commit. "Deployed" through authorization is AC-033/034's, in Phase 9 | Phase 5's predictions are advisory twice over — shadow, and no Supervisor yet; AC-008's wording gets a scope note |
| **B** | Split as OD-007 did: a new Phase 5 criterion for traceability; AC-008 whole moves to Phase 9 | criterion churn for a claim A already proves honestly |
| **C** | Build a minimal publisher in Phase 5 | Phase 9's lifecycle work, half done, four phases early |

**Recommendation: A.** Traceability is what AC-008 measures; authorization is what Phase 9 adds. The
`SHADOW` restriction keeps Phase 5 from claiming an authority nothing granted.

## Decision — A, with Codex's conditions

Codex: `SOUND_WITH_CONDITIONS` (`reviews/phase-5/CODEX_OD_CHALLENGE_raw.md`); confirmed by the product owner 2026-10-07.

1. Phase 5 documents say **configured `SHADOW` model**, not "authorized model".
2. Any configured `deploymentStage` other than `SHADOW` fails startup in Phase 5.
3. Phase 5 neither consumes nor produces `factory.model-deployments.v1`.
4. AC-008 is proven as traceability: a prediction's `model.name`, `version` and `runId` resolve in
   MLflow to a run that records the dataset hash, feature schema version and git commit.

---

# OD-027 — RESOLVED 2026-10-07 — option A — a prediction's identity must survive a restart and a replay

> Raised 2026-10-07 by the Phase 5 Definition-of-Ready check (`P5-DOR-007`, P1).

## The problem

`predictionId` is the topic's duplicate identity; `featureWindow.windowId` is the causation; the
prediction mints the correlation ID (OD-013); `predictedAtUtc` must be strictly increasing per
equipment (DEC-003). Nothing says whether these are the same when the service restarts and computes
the same window again, or when the feature-builder group is replayed offline. If they are random, every
recomputation is a new prediction: the topic gains duplicates no consumer can recognise, and the
operations projection's first-wins (OD-023) keeps whichever came first for reasons of timing.

## Options

| | Option | Cost |
|---|---|---|
| **A** | **Identities are derived; time is not.** Windows end on absolute 5 s event-time boundaries. `windowId` = UUIDv5 of `(equipmentId, windowEndUtc, featureSchemaVersion)`; `predictionId` = UUIDv5 of `(windowId, modelName, modelVersion, deploymentStage)`; `correlationId` = UUIDv5 of `predictionId` under a second namespace. The same window and model always give the same IDs, so a recomputation is a duplicate by the topic's own rule. `occurredAtUtc` is the window end (event time). `predictedAtUtc` stays the inference wall clock, clamped strictly increasing — it is what the Supervisor's TTL and supersession need, and is not part of identity | two namespaces fixed in the contract; a replayed prediction carries a new `predictedAtUtc` under an old identity, which first-wins resolves |
| **B** | Random identities; consumers dedupe on `(equipmentId, windowEnd, model)` | a second duplicate identity beside the contract's, re-implemented by each consumer |
| **C** | Random, and accept duplicates | the projection's audit and the trace show phantom predictions |

**Recommendation: A.**

## Decision — A, amended by Codex's conditions

Codex: `SOUND_WITH_CONDITIONS` (`reviews/phase-5/CODEX_OD_CHALLENGE_raw.md`); confirmed by the product owner 2026-10-07.

1. **`runId` is part of `predictionId`**: a registry version is not proven to name one artifact forever.
2. **Fixed namespaces and encoding** (`EVENT_CONTRACTS.md` §2.1): fields joined with `|`, UTF-8,
   timestamps as `yyyy-MM-ddTHH:mm:ss.fffZ`, integers in decimal.
   - `windowId` = UUIDv5(`f5fb85eb-c84a-4699-b8a9-870c33844a0d`, `equipmentId|windowEndUtc|featureSchemaVersion`)
   - `predictionId` = UUIDv5(`c06613dd-4f77-4349-8613-0bf116399014`, `windowId|modelName|modelVersion|runId|deploymentStage`)
   - `correlationId` = UUIDv5(`8dea0b34-62ea-4875-b589-1ba6bcfa7a00`, `predictionId`)
3. **`eventId` is per emission** and random: it is not the duplicate identity, and a recomputation
   carries a new one under the same `predictionId`.
4. `predictedAtUtc`'s strict monotonicity is a **guard**, not the ordering's foundation: DEC-003 makes
   timestamp ordering secondary to TTL, cadence and epoch fencing. The schema says so (`P5-ODC-002`).

---

# OD-028 — RESOLVED 2026-10-07 — option A — what a feature is, how it is aggregated, and what "byte-identical" compares

> Raised 2026-10-07 by the Phase 5 Definition-of-Ready check (`P5-DOR-008`, `P5-DOR-009`, P1).

## The problem

`TIME_AND_DATA_QUALITY.md` §8 and `PREDICTION_SERVICE.md` §10 fix the bucket (absolute event-time
seconds), null exclusion and the expected-count denominator. They do not fix: **which features**; when
a bucket is **final**; what a **late** record does; duplicates; the expected count when cadence drifts
from 10 Hz; and AC-032's **byte** representation — between which two paths when both are Python.

## Options

| | Option | Cost |
|---|---|---|
| **A** | **One definition in `mair_ml_core`, versioned by `featureSchemaVersion`.** Per channel over the 60 s window: mean, standard deviation, min, max, last, and slope of the 1 s bucket means; plus `validSampleRatio`. Buckets: event time floored to the second; duplicates by `(equipmentId, sequence)` dropped; a bucket is **final** once a record of the same equipment arrives 5 s past its end, in partition order — event time, not wall clock, so a replay decides the same; a record for a final bucket is dropped and counted. Expected count = window seconds × 10 (the contract cadence); the ratio is capped at 1. **AC-032** compares the **canonical feature vector**: a JSON array in the schema's fixed order, each float as its shortest round-trip text, produced once by the **batch** path over a dataset file and once by the **streaming** path over the same records arriving one by one | the late-record rule loses a late sample rather than revising a window already used for inference |
| **B** | Raw-sample features, no 1 s buckets | contradicts `PREDICTION_SERVICE.md` §10 and §8's rules, which exist for restart invariance |

**Recommendation: A.**

## Decision — A, with Codex's conditions

Codex: `SOUND_WITH_CONDITIONS` (`reviews/phase-5/CODEX_OD_CHALLENGE_raw.md`); confirmed by the product owner 2026-10-07.

1. **No inference on a window until every bucket in it is final** under the event-time,
   partition-order rule.
2. **Restart recovery replays records through the same finalisation and late-drop logic** — never a
   rebuild by sorting on event time.
3. Late-dropped records are **counted and asserted** in tests, not only exported.
4. Duplicate removal uses the telemetry duplicate identity **as `OD-030` corrects it**:
   `(equipmentId, sequence)` alone collides across an equipment restart.

---

# OD-029 — RESOLVED 2026-10-07 — option A′ (Codex) — the models' outputs have names and ranges, and no definitions

> Raised 2026-10-07 by the Phase 5 Definition-of-Ready check (`P5-DOR-010`, P2), widened while checking
> `prediction.schema.json`: the algorithm choice was open, and so were most outputs.

## The problem

`AI_SAFETY_AND_MLOPS.md` §1 offers "Isolation Forest, autoencoder, or a robust statistical baseline"
for anomaly and "LightGBM" for failure/RUL. The schema requires `anomalyScore`, `confidence` and
`oodScore`, and allows `failureProbability`, `rulMinutes` and `recommendedOperationRatePct` — but
`confidence` has no definition, `oodScore` no scale (the example threshold is 0.35),
`failureProbability` no horizon, and the recommendation no rule. Each of those is read by a safety
gate in Phase 6.

## Options

| | Option | Cost |
|---|---|---|
| **A** | **Statistical and tree baselines, every output defined.** `oodScore` = fraction of features outside the training distribution's robust envelope (median ± 4·MAD), in [0, 1] — baseline decision #5's envelope, made a number. `anomalyScore` = the same robust distance of the feature vector, squashed to [0, 1]. `failureProbability` = P(failure within **30 min**), from a gradient-boosted classifier; `rulMinutes` from a gradient-boosted regressor on `T_fail − t`, both only for windows the model was trained to judge and `null` otherwise. `confidence` = 1 − `oodScore` × 2, floored at 0 — a model is least sure where its inputs are least familiar. `recommendedOperationRatePct` = `null` when `failureProbability` is `null`, else 100 − 40 × `failureProbability`, to 0.1 — within the 60–100 % operating bounds by construction, the Supervisor still bounding it. The gradient-boosting library is an ADR choice (P5-DOR-002) | definitions chosen for being explainable and testable, not optimal; each threshold is a starting point the promotion gates then measure |
| **B** | Leave definitions to the model and the training run | Phase 6's gates would read numbers nobody defined |
| **C** | Emit only `anomalyScore`, `confidence`, `oodScore`; the others `null` until a later phase | the RUL promotion gate and Phase 6's rate path have nothing to measure |

**Recommendation: A**, with the explicit note that the numbers (4·MAD, 30 min, 40 pp) are portfolio
starting points under `AI_SAFETY_AND_MLOPS.md` §7's gates, not tuned values.

## Decision — A′, the challenger's position, confirmed by the product owner (2026-10-07)

Codex: `WRONG_OPTION` for A. Deriving `confidence` from `oodScore` would make gate 9 re-run gate 8 in
disguise; the canonical gate table keeps them separate for a reason, and so does this decision. Every
output is defined; none is derived from another gate's input.

| Output | Definition |
|---|---|
| `oodScore` | the fraction of the **safety-required channels'** features outside the training distribution's robust envelope, median ± 4 × MAD. A feature whose training MAD is 0 uses MAD = max(1 % of \|median\|, 1e-9). Features of an advisory channel below 0.8 `validSampleRatio` are left out of the fraction. In [0, 1] by construction |
| `anomalyScore` | the robust distance of the feature vector from the training median, mapped to [0, 1] by `d / (d + 1)` |
| `failureProbability` | P(failure within **30 min**), a calibrated gradient-boosted classifier, labelled from the manifest's `T_fail`; `null` when the model abstains |
| `rulMinutes` | `T_fail − t` predicted by a gradient-boosted regressor; `null` when the model abstains |
| `confidence` | **the failure classifier's own certainty**, \|2p − 1\| for its calibrated p; **0** when it abstains, so gate 9 fails closed. Independent of `oodScore` |
| `recommendedOperationRatePct` | `null` when `failureProbability` is `null`; **100** when it is below 0.1 (no reason to slow down); otherwise 100 − 40 × p, to 0.1, never below 60. The Supervisor still bounds it |

The numbers (4 × MAD, 30 min, 0.1, 40 pp) are portfolio starting points that `AI_SAFETY_AND_MLOPS.md`
§7's promotion gates then measure, not tuned values. The gradient-boosting library is an ADR choice
(`P5-DOR-002`).

---

# OD-030 — OPEN — the telemetry duplicate identity collides across an equipment restart

> Raised 2026-10-07 by Codex's challenge of OD-024–029 (`P5-ODC-001`, `P5-ODC-003`, P1), confirmed
> against the contracts. A defect since Phase 2; it surfaces now because Phase 5 is the first consumer
> that de-duplicates telemetry by its identity.

## The problem

`KAFKA_TOPOLOGY_AND_SEMANTICS.md` §7 makes `(equipmentId, sequence)` the duplicate identity of
`factory.telemetry.v1`. §8 and `EDGE_GATEWAY.md` §5.2: `sequence` **resets to 0 on an equipment
restart** (and on the 2^32 ms epoch wrap, where the simulator resets it too). After a restart the same
identity names a different sample. A consumer that drops "duplicates" within a 60 s window would drop
real post-restart samples.

The canonical record cannot tell the two apart. It carries no restart discriminator: `sourceEpochMs`
(milliseconds since equipment start) is read by the gateway and not emitted, and it would not help if
it were — a deterministic machine produces sequence *n* at the same milliseconds-since-start in every
boot, so `(sourceEpochMs, sequence)` collides too. What is missing is a name for **the boot**.

The telemetry schema has a producer (the gateway, Phase 3), so only an additive optional field is a
`v1` change (`EVENT_CONTRACTS.md` §5).

## Options

| | Option | Cost |
|---|---|---|
| **A** | **The gateway names the boot.** It already detects a restart — `sequence` and `sourceEpochMs` both going backwards (`EDGE_GATEWAY.md` §5.2). It emits two additive optional fields: `gatewayEpoch` (as OD-008 defined it for the state stream) and `sourceBoot`, a per-equipment counter it increments on each detected restart. The identity becomes `(equipmentId, gatewayEpoch, sourceBoot, sequence)` | gateway-only, no protocol change; a sample re-emitted across a **gateway** restart gets a new identity — rare, since the gateway reads current values, not history |
| **B** | **The equipment names the boot.** A boot ID register (Modbus) and node (OPC UA), set at each start; the gateway emits it; identity `(equipmentId, bootId, sequence)` | truth at the source and stable across gateway restarts; changes the OT mapping, the Modbus block length, the simulator and both protocol paths — Phase 2's proven code |
| **C** | Keep the identity; every consumer detects resets itself, as the gateway does | the rule re-implemented per consumer — the divergence OD-009 and OD-024 exist to prevent |

**Recommendation: A**, unless re-emission across a gateway restart must be recognised as a duplicate —
then B. The identity change is recorded as a correction of a defective definition, with the old pair
still unique within one boot.

---

## Found while writing OD-011–018, and fixed directly

`FaultInjectionRequest.profile` in the OpenAPI contract listed ten profiles. `OD-001` added
`DRIVE_STUCK` on 2026-09-15 and the simulator implements it, so the contract had drifted from a
resolved decision. The enum now lists eleven. No consumer exists (the route is Phase 11, AC-031).
