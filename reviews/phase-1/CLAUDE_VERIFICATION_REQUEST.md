# Phase 1 — Verification request to Codex

> Round: formal verification, read-only. `DUAL_AGENT_PROTOCOL.md` §1: Codex may recommend a fix;
> Claude implements it. A reviewer that edits the code is reviewing its own work on the next pass.
>
> Mandatory participation is triggered by `DUAL_AGENT_PROTOCOL.md` §2 — this change affects
> **equipment safety**, **fallback logic**, and **failure recovery**.

## What was built

Commit `8d24190`. The equipment simulator **domain core**, in `src/dotnet/EquipmentSimulator/`:

| File | Contents |
|---|---|
| `Model.cs` | enums, `RawSample`, `ChannelFlag`, `FaultEvent` |
| `EquipmentOptions.cs` | per-equipment configuration + fail-closed validation |
| `DeterministicRandom.cs` | xoshiro256** seeded by SplitMix64, Box–Muller Gaussian |
| `EquipmentSimulation.cs` | physics, state machine, fault profiles, L3 self-protection, dead-man |

44 unit tests in `src/dotnet/EquipmentSimulator.Tests/`, all passing
(`dotnet test src/dotnet/Mair.sln`).

**Deliberately not in Phase 1:** OPC UA / Modbus server endpoints, Kafka publication, the 100 ms
loop host, the admin fault-injection network API. See `IMPLEMENTATION_PLAN.md` Phase 1 scope note.

## Authoritative documents

- `docs/02-architecture/EQUIPMENT_MODEL_AND_STATE.md` — physics constants (§1.3), slew (§1.4),
  fault profiles (§2), state machine and transition table (§3), protective conditions (§3.4),
  safety-required vs advisory sensor map (§4)
- `docs/11-service-design/EQUIPMENT_SIMULATOR.md` — service design, failure behaviour (§11)
- `docs/01-requirements/ACCEPTANCE_CRITERIA.md` — AC-018, AC-019, AC-020
- `docs/06-development/TEST_SPECIFICATIONS.md` — PROP-03
- `docs/09-decisions/DEC-001-fallback-authority.md`, `docs/07-adr/ADR-0011-*`,
  `ADR-0015-ood-hard-reject.md`, `ADR-0018-nullable-measurements-closed-quality.md`

## What to challenge

Please do **not** limit yourself to this list, but do cover it.

1. **Physics fidelity.** Does every formula in `Measure()` / `AdvancePhysics()` match §1.3 and §1.4
   exactly — coefficients, the `h` and `c` exponents, the thermal first-order lag, the slew clamp?
   Is the discrete integration of `T` the one the document specifies, or a subtly different one?

2. **Three changes Claude made to the model while implementing.** Each is documented, but each is
   also a place where the implementer altered the specification it was implementing. Judge them:
   - **Physical floor on additive noise.** Readings are clamped at a channel's lower bound where
     that bound is 0. Motivation: at rest `currentA = 0 ± N(0, 0.06)` goes negative, which is
     `VALUE_OUT_OF_RANGE` on a safety-required channel, so an idle machine tripped itself after 2 s.
     Is clamping the right fix, or does it mask a class of genuine fault? Should the upper bound
     have been clamped too, or is leaving it unclamped correct?
   - **Rate-deviation degradation check** (§3.3, last bullet) now compares the observed rate against
     the **slew-limited expected rate** rather than the commanded value. Motivation: a 0→100 % start
     takes 6.7 s at 15 %/s, so the literal reading puts every cold start into `DEGRADED` for the 30 s
     `T8` window, contradicting `T4`. But note the consequence: with no drive-fault profile in the
     model, `expected == applied` always, so **this condition can never fire**. Is that acceptable,
     or has a safety condition been silently disabled?
   - **Over-temperature margin.** `COOLING_DEGRADATION` at full rate asymptotes at 120.46 °C against
     a 120 °C trip. Is a 0.46 °C margin a defect in the constants, in the trip threshold, or neither?

3. **State machine.** Are all of T1–T12 implemented, and are the three forbidden transitions
   actually unreachable — not merely untested? Specifically:
   - can anything reach `RUNNING` from `FAULT` without `OperatorReset`?
   - can anything reach `RUNNING` from `OFFLINE` without passing through `CONNECTING`?
   - `EvaluateState` skips protective evaluation in `STOPPING`. Is that right? §3.2 T9 lists
     `RUNNING`/`DEGRADED`/`IDLE`, but a machine slewing down through an over-temperature condition
     seems like it should still trip.
   - `PresentProtectiveConditions()` (used by `OperatorReset`) checks a **different** set of
     conditions than `UpdateAndCollectProtectiveConditions()` (used per tick). Is that divergence a
     bug? Can an operator reset a machine whose over-current or bad-quality condition is still live?

4. **L3 independence (AC-020).** The claim "protective trips work with the entire platform stopped"
   is asserted by a test that checks the assembly has no non-framework referenced assemblies. Is
   that a sufficient proof of the property, or does it prove something weaker?

5. **Dead-man (DEC-001).** It fires only when `_setpointPct > 0`, so it never starts an idle
   machine, and it reverts to `SafeDefaultRatePct` (60 %). Consider: a drive running at 50 % would be
   **raised** to 60 % by the dead-man. Is that reachable, and is it correct? Is resetting
   `_ticksSinceSetpointWrite` on revert right, or should the dead-man latch?

6. **Determinism (NFR-010 / PROP-03).** Noise is drawn unconditionally in a fixed order, and the
   second Box–Muller variate is discarded rather than cached. Is the run genuinely a pure function
   of (seed, command sequence, tick count)? Is anything in the code order-dependent on a
   `HashSet`/`Dictionary` iteration, a `double` formatting decision, or culture?

7. **`sequence` / `sourceEpochMs`.** The wrap logic resets `_sequence` when `_elapsedMs % 2^32 == 0`.
   Check the boundary arithmetic. Does the sample emitted at the wrap carry consistent values, or is
   there an off-by-one where a consumer sees a gap or a duplicate?

8. **Do the tests prove the AC, or only appear to?** This is the question that matters most.
   In particular: `AC-018` claims all 10 fault profiles produce "the documented signature" — do the
   assertions actually pin the documented behaviour, or are the tolerance bands wide enough to pass
   a wrong implementation?

## Output format

For each finding: `ID · severity (P0/P1/P2/P3) · file:line · what is wrong · why it matters ·
recommended fix`. End with a verdict line.

`P0` = a safety or correctness defect in what was built. `P1` = a specification contradiction or a
test that does not prove what it claims. Do not inflate severity, and do not withhold a finding
because the work is already committed.
