# Round 2 — Claude classifies every Codex finding

> `DUAL_AGENT_PROTOCOL.md` §3: every finding is classified `ACCEPTED`, `PARTIALLY_ACCEPTED`,
> `REJECTED_WITH_EVIDENCE`, or `HUMAN_DECISION_REQUIRED`. **None may be silently dropped.**
>
> Codex verdict on commit `8d24190`: **REJECT** — 1 P0, 5 P1.
> Findings: [`CODEX_VERIFICATION_raw.md`](CODEX_VERIFICATION_raw.md).

| ID | Sev | Classification | Fixed in |
|---|---|---|---|
| COD-VFY-001 | P0 | **ACCEPTED** | `EquipmentSimulation.cs` reset guard |
| COD-VFY-002 | P1 | **PARTIALLY_ACCEPTED** | `InjectCommLoss` profile gate |
| COD-VFY-003 | P1 | **ACCEPTED** | `EpochWrapped` |
| COD-VFY-004 | P1 | **ACCEPTED**, with a `HUMAN_DECISION_REQUIRED` spun out as OD-001 | independent expected-rate integrator |
| COD-VFY-005 | P1 | **ACCEPTED** | `T9` extended to `STOPPING`, document and code |
| COD-VFY-006 | P1 | **PARTIALLY_ACCEPTED** | behavioural L3 test added, overclaiming comment corrected |

Nothing was rejected. Codex was right on all six.

---

## COD-VFY-001 — reset could clear a live safety fault · **ACCEPTED** (P0)

`OperatorReset()` derived its own set of "conditions present now" — injected conditions,
over-temperature, `STOP_REQUIRED` — while the tick path used a different, larger set. A machine
faulted by a dead `vibrationRms` sensor could therefore be reset straight back to `IDLE` and would
accept setpoints for the entire 2 s re-trip window. That violates `T10`, which requires operator
reset **and** all protective conditions clear.

The root cause is not the missing conditions; it is that **two independent evaluations of "is this
machine safe" existed**, and the one guarding the reset was the weaker of the two. Adding the two
missing conditions would have left the same defect one edit away. The reset now reads the set the
tick path just computed, so the two cannot diverge.

`STOP_REQUIRED` stays excluded, and this is deliberate: §3.4 calls it an explicit control mode
entered by an operator, not a sensed condition, so acknowledging the reset is what exits it.

Tests: `OperatorReset_IsRefusedWhileASafetyRequiredSensorIsStillDead`,
`OperatorReset_IsRefusedWhileOverCurrentIsStillLive`.

## COD-VFY-002 — `COMM_LOSS` profile was inert · **PARTIALLY_ACCEPTED** (P1)

**Accepted:** the profile did nothing. `_commLost` was reachable only through the demo injection, so
`CommLoss_StopsAnsweringWhilePhysicsKeepsRunning` proved the injection API existed, not that the
documented profile produces its signature. Codex is right that this is not an AC-018 test.

**Partial:** Codex recommends the profile itself stop the endpoint. That would fix the test and
break the model. `EQUIPMENT_SIMULATOR.md` §5 gives the simulator a "fault-injection admin API
(demo only)", which is precisely the mechanism that decides *when* a fault occurs; §2 describes
*what* each profile does. Onset belongs to injection. A self-triggering profile would also need an
undocumented onset constant, and would make AC-002 — disconnect, restore, reconnect within 10 s —
harder to express, not easier.

What was actually wrong is the missing link between the two: any equipment could lose comms
regardless of its configured profile. `InjectCommLoss()` now requires `FaultProfile.CommLoss`, so
the profile is what makes the fault *possible* and injection is what makes it *happen*. The test now
proves the profile gates the behaviour.

Test: `CommLoss_CanOnlyBeInjectedIntoEquipmentConfiguredForIt`.

## COD-VFY-003 — epoch wrap arithmetic · **ACCEPTED** (P1)

Verified independently before accepting. `_elapsedMs` advances in steps of 100 and is therefore
always a multiple of 100; `2^32 = 4294967296` is not. `_elapsedMs % 2^32 == 0` can only hold at
`LCM(2^32, 100) = 107374182400 ms`, so **24 of every 25 rollovers reset nothing** and a consumer
would see `sourceEpochMs` jump to a small value while `sequence` kept climbing — indistinguishable
from a gap, which is the one thing §11 says the simultaneous reset exists to prevent.

The wrap decision is now a pure function, `EquipmentSimulation.EpochWrapped`, because reaching a
real rollover through `Tick()` takes 43 billion calls.
`EpochWrap_HoldsForEveryOneOfTheFirstWraps_NotOnlyEveryTwentyFifth` walks all 25 and fails on the
first 24 against the old logic.

## COD-VFY-004 — tracking condition was unreachable · **ACCEPTED** (P1), and it exposed a spec gap

`_expectedPct = _appliedPct` made `Math.Abs(_appliedPct - _expectedPct) > 10` a tautology. The
condition was documented, appeared implemented, and could never fire — the worst of the three
states. Two independent integrators now run from the setpoint, so `expected` is what the drive
**owes** its setpoint and `applied` is what it **did**.

Fixing it exposed the real problem, which the finding points at but does not state: **no fault
profile in §2 can make a drive stop tracking.** A safety condition whose only possible cause is a
test hook is not validated by any scenario the demo actually runs. Raised as
**`OPEN_DECISIONS.md` OD-001** with three options; it needs a product decision because the
recommended option changes AC-018 from ten profiles to eleven. A demo-gated `InjectDriveLag()` makes
the condition provable in the meantime.

Tests: `DriveThatStopsTrackingItsSetpoint_Degrades_AfterSlewGrace`, plus
`HealthySlew_StillDoesNotDegrade` so the fix cannot reintroduce the cold-start problem that the
original change was made to avoid.

## COD-VFY-005 — `STOPPING` could not latch `FAULT` · **ACCEPTED** (P1)

A genuine contradiction inside `EQUIPMENT_MODEL_AND_STATE.md`: §3.4 says any protective condition is
sufficient for `→ FAULT`, while `T9`'s source states omitted `STOPPING`. The implementation followed
the table. Consequence, exactly as Codex describes: a machine slewing down through an
over-temperature condition reaches `IDLE` without latching, and if the condition clears on the way
down it never required operator acknowledgement at all.

The prose was right and the table was wrong, so the table was corrected — `T9` now includes
`STOPPING`, in the transition table and in the state diagram, with the reasoning recorded in §3.4.
Documentation first, then code.

Test: `ProtectiveConditionDuringAStop_StillLatchesFault`.

## COD-VFY-006 — the L3 proof was weaker than claimed · **PARTIALLY_ACCEPTED** (P1)

**Accepted:** the assembly-reference check proves the simulator *can* run with nothing else present.
It does not prove the protective trips and the dead-man *do* run in that condition, and the test
comment claimed it did. The comment overclaimed and has been corrected.

**Partial:** Codex recommends adding behavioural tests "using only `EquipmentSimulation.Tick()` and
no external fixtures". Those already existed — `ProtectiveConditionTests` and `ActuationTests`
construct nothing but the simulation and call `Tick()`. What was missing was any test that *says*
this is the AC-020 argument, so the two halves were never connected.

`L3Behaviour_HoldsWithNothingButTicks` makes the pairing explicit: a protective trip and a dead-man
revert, driven only by ticks, with no host, no clock, no broker, no supervisor and no control
service in the process. The structural check stays, narrowed to what it actually proves.

---

## Result

56 tests pass (`dotnet test src/dotnet/Mair.sln`). Every finding is addressed; one is carried to the
product owner as OD-001 rather than closed by an implementer's judgement.

Codex could not execute the suite itself — MSBuild could not create a temp directory under the
read-only sandbox. It reported that as a sandbox limitation instead of inferring a result, which is
the correct behaviour and worth recording.
