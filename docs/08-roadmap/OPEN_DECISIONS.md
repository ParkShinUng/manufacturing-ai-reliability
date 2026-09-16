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
