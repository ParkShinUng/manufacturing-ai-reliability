# Round 3 — Claude responds to the Codex re-evaluation

> Codex round 2 verdict: **REJECT** — `COD-VFY-002` not closed, `004`/`006` partial, plus two new
> findings. Raw: [`CODEX_ROUND2_raw.md`](CODEX_ROUND2_raw.md).
> `DUAL_AGENT_PROTOCOL.md` §3: **maximum 3 rounds**, then remaining disagreement escalates to the
> human. This is round 3.

| ID | Sev | Round 2 status | Round 3 classification |
|---|---|---|---|
| COD-VFY-001 | P0 | partially closed | **ACCEPTED** — residual closed with a non-injected test |
| COD-VFY-002 | P1 | **not closed** | **ACCEPTED IN FULL** — my round 2 position was wrong |
| COD-VFY-003 | P1 | closed | closed |
| COD-VFY-004 | P1 | partially closed | **HUMAN_DECISION_REQUIRED** — OD-001 |
| COD-VFY-005 | P1 | closed | closed |
| COD-VFY-006 | P1 | partially closed | **HUMAN_DECISION_REQUIRED** — OD-002, see below |
| COD-R2-001 | P1 | new | **ACCEPTED** — a regression I introduced in round 1 |
| COD-R2-002 | P2 | new | **ACCEPTED** |

---

## COD-R2-001 — the round 1 fix created a way to defeat a protective trip · ACCEPTED (P1)

This one is mine. `InjectDriveLag()` froze `_appliedPct`, and the protective trip sets only
`_setpointPct = 0`, so a faulted machine kept running at speed. A **demo hook could defeat "FAULT:
rate forced 0"** — the single property AC-020 exists to guarantee, defeated by the test affordance
added to prove a different finding.

The fix is not to clear the injection on trip but to make the safety path unblockable: in `FAULT`
the applied rate slews toward zero regardless of what any injection says. A protective trip cuts the
drive, so a drive fault is irrelevant at that point anyway.

Test: `ProtectiveTrip_ForcesRateToZeroEvenWhileDriveLagIsInjected`.

## COD-R2-002 — reset guard read stale state · ACCEPTED (P2)

The guard reads the previous tick's evaluation, so an injection landing between ticks was invisible.
The injected set is now unioned in synchronously. The remaining asymmetry is deliberate and
recorded in the code: stale state may only cause a reset to be **refused**, never granted.

Test: `ConditionInjectedBetweenTicks_StillBlocksReset`.

## COD-VFY-002 — I was wrong · ACCEPTED IN FULL

In round 2 I argued that onset belongs to the injection API (§5) and that gating injection on the
profile was sufficient. Codex re-asserted with the text that settles it: **AC-018 requires each
*profile* to produce its documented signature**, and §2's model for `COMM_LOSS` is "protocol
endpoint stops responding". Gating an injection proves a permission check, not a signature.

`FaultProfile.CommLoss` now stops the endpoint answering by itself, from construction. The demo API
remains for the disconnect-then-restore cycle, which is AC-002 and therefore Phase 2. The signature
test no longer injects anything.

Test: `CommLoss_ProfileAloneStopsTheEndpointAnswering`.

## COD-VFY-001 residual — the over-current regression proved nothing · ACCEPTED

Codex is right: that test used an **injected** over-current, which the old guard already saw, so it
could not have caught the original gap. Replaced with a path that uses no injection at all —
`SENSOR_DRIFT` on `currentA` raises the reading past 32 A after about 25 minutes of simulated time.

Finding that path is what produced OD-002 below.

Test: `OverCurrent_TripsAndGuardsTheReset_WithNoInjectionAtAll`.

## COD-VFY-006 residual — and what it actually uncovered · HUMAN_DECISION_REQUIRED (OD-002)

Codex's point was that the new behavioural L3 test would have passed against the old code, so it
characterises rather than regresses, and that it does not exercise every AC-020 protective
condition. Both true. Trying to fix it properly — by driving each condition from physics instead of
an injection — produced the real finding:

**Two of the three sensed protective thresholds cannot be reached by any of the ten fault profiles.**

| Condition | Threshold | Maximum the model can produce | Reachable? |
|---|---|---|---|
| over-temperature | 120 °C | 120.46 °C (`COOLING_DEGRADATION`, `c` capped at 0.9, full rate) | barely — 0.46 °C, ~20 min |
| over-vibration | 25.0 mm/s | 15.4 mm/s (`BEARING_DEGRADATION` at `h` = 1) | **no** |
| over-current | 32 A | 19.2 A (`OVERLOAD`, ×1.6) | **not as real current** — only via `SENSOR_DRIFT` on `currentA`, which is a sensor fault, not an over-current |

This is why the AC-020 tests reach for injection: **the model's own fault profiles cannot exercise
the protective layer.** That is not a testing problem to be worked around, it is a statement about
the simulator — the innermost safety layer, the one DEC-001 says must survive total platform loss,
is currently unexercisable by anything the demo runs.

Raised as **`OPEN_DECISIONS.md` OD-002**. It needs a product decision because both candidate fixes
change normative constants: either lower the thresholds into the model's range, or extend the
profiles to reach them. An implementer must not choose.

## COD-VFY-004 — unchanged · HUMAN_DECISION_REQUIRED (OD-001)

Codex calls the partial closure acceptable as an interim. It stays open as OD-001 for the same
reason as OD-002: closing it changes AC-018's profile count, which is the product owner's call.

---

## Result and escalation

59 tests pass. Every P0 and every P1 defect **in what was built** is now closed:
`COD-VFY-001, 002, 003, 005` and `COD-R2-001, 002`.

What remains is not defective code. It is two places where the **specification** leaves a safety
condition with no cause in the model:

- **OD-001** — no fault profile can make a drive stop tracking its setpoint
- **OD-002** — the over-vibration and over-current protective thresholds are above anything the
  model can produce

Per `DUAL_AGENT_PROTOCOL.md` §3, three rounds are spent and both are architecture/safety questions,
so they go to the product owner rather than to a fourth round. Phase 1 should not be marked done
until they are answered: a protective layer that nothing can trigger is not a verified protective
layer, whatever the test count says.
