# Service Design — `equipment-simulator`

> Runtime: C#/.NET 10. Phase 1 primary deliverable. L3 in the authority model.
> Model constants: `docs/02-architecture/EQUIPMENT_MODEL_AND_STATE.md`.
> Protocol contract: `docs/03-contracts/OT_PROTOCOL_MAPPING.md`.

## 1. Purpose
Behavioural Digital Twin / HIL (ADR-0004). Owns equipment physics, state, fault injection, and
**equipment self-protection** — the innermost safety layer, which survives total platform loss.

## 2. Responsibilities
Physics integration at 100 ms; equipment state machine (T1–T12); OPC UA + Modbus TCP server
endpoints; deterministic seeded fault injection; protective trips; **dead-man setpoint revert**;
`sequence` and `sourceEpochMs` assignment; fault events to `factory.faults.v1`.

## 3. Non-responsibilities
No normalisation (gateway's job); no Kafka telemetry production; no AI; no canonical quality
derivation — it reports raw protocol quality only.

## 4. Dependencies
| Dependency | Class | Failure behaviour |
|---|---|---|
| none for physics/state | — | the simulator is self-contained by design, so L3 protection cannot be lost with the platform |
| Kafka (fault events only) | Non-control-critical | buffer; never blocks the model loop |

## 5. Inputs / outputs
**In:** setpoint write (OPC UA node / Modbus holding register); fault-injection admin API (demo only).
**Out:** OPC UA + Modbus telemetry; `factory.faults.v1`.

## 6. Contracts
`OT_PROTOCOL_MAPPING.md` (authoritative address maps); `contracts/jsonschema/v1/` fault record.

## 7. Data ownership
Owns true physical state (`h`, `c`, temperatures, applied rate), `sequence`, `sourceEpochMs`,
fault-injection state. This is the **only** ground truth in the system; everything else is observed.

## 8. State model
Per equipment: `r_sp`, `r_applied`, `T`, `h`, `c`, `state`, `sequence`, `sourceEpochMs`,
`activeFaultProfile`, `seed`. States and transitions: `EQUIPMENT_MODEL_AND_STATE.md` §3.

## 9. Lifecycle
Load equipment profiles and seeds → initialise at `IDLE` → start protocol servers → begin the 100 ms
integration loop → accept setpoints.

## 10. Normal flow
```text
every 100ms per equipment:
  r_applied += clamp(r_sp - r_applied, +/- 15%/s * dt)   # slew
  advance h(t), c(t) per active fault profile
  T += (T_target - T) * dt / 180s
  compute rpm/torque/current/voltage/vibration + noise
  apply sensor fault overlays (drift/noise/freeze/dropout)
  evaluate protective conditions -> possibly FAULT
  evaluate dead-man -> possibly revert setpoint
  sequence += 1 ; publish to OPC UA + Modbus register image
```

## 11. Failure behaviour
| Failure | Behaviour |
|---|---|
| No setpoint refresh for 30 s | **dead-man**: revert to safe default rate (L3, DEC-001) |
| Protective condition | latch `FAULT`, force rate 0, require operator reset (T10) |
| Out-of-range write | Modbus exception 0x03 / OPC UA `Bad_OutOfRange`; **never silently clamped** |
| Kafka down | buffer fault events; model loop unaffected |
| `sourceEpochMs` wrap (~49.7 d) | reset `sequence` simultaneously so the two signals stay consistent |

Rejecting rather than clamping an out-of-range write is deliberate: clamping would mask a
control-path defect that the Control Service bounds check should have caught first.

## 12. Timeout / retry / idempotency / ordering
Setpoint write is **naturally idempotent** (it is a setpoint, not a delta) — this is what makes
Control Service retry safe. Loop deadline 100 ms; an overrun is counted, never silently skipped.
Ordering: last write wins within a 100 ms tick.

## 13. Backpressure
Fixed-rate loop; no inbound queue. Fault-event buffer 1 000, drop oldest.

## 14. Restart recovery
`sequence` resets to 0 **with** `sourceEpochMs`, so consumers can distinguish a restart from a gap.
Equipment restarts at `IDLE`, never at `RUNNING` — a simulator that restarts already running would
hide a real startup race.

## 15. Configuration
`equipmentCount` (demo 20, load ≤ 250), per-equipment `seed`, `faultProfile`, `T_fail`, `T_cool`,
`deadmanTimeoutMs` (30 000), `safeDefaultRatePct` (60), `demoProfile` (bool).

## 16. Security
Fault-injection and admin endpoints exist **only** when `demoProfile=true` and are absent from the
build otherwise. Only the setpoint node/register is writable. The gateway's OT session is provisioned
**without** write permission, so read-only gateway behaviour is server-enforced rather than trusted.

## 17. Observability
`simulator_loop_overruns_total`, `equipment_state{state}`, `fault_injections_total{profile}`,
`deadman_reverts_total`, `protective_trips_total{condition}`, `setpoint_writes_total{result}`.

## 18. Performance targets (TARGET — unmeasured)
20 equipment @100 ms with loop overrun < 0.1 %; 250 equipment @100 ms with overrun < 1 %;
protocol read P95 < 20 ms.

## 19. Test strategy
Unit: physics determinism under a fixed seed; slew limiter; every T1–T12 transition; protective
conditions; dead-man. Integration: OPC UA and Modbus clients read identical engineering values
(this is the test that catches word-order and scaling errors). Property: identical seed ⇒
bit-identical telemetry sequence (NFR-010).

## 20. Acceptance criteria
AC-001, AC-002, AC-018, AC-019, AC-020.
