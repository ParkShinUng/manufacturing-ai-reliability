# OD-007 / OD-008 / OD-009 — challenge the intended decisions, before they are recorded

> Read-only challenge. The product owner has said which option they intend to take on each and asked
> for a joint re-review **before** the decisions are written into the documents. Nothing is settled;
> if an option is wrong, say so, and say what the better one costs.
>
> The options and their costs as put to the product owner are in `docs/08-roadmap/OPEN_DECISIONS.md`
> under `OD-007`, `OD-008`, `OD-009`. Your own findings that produced them are in
> `CODEX_DOR_CHALLENGE_raw.md`.

## Intended decisions

| | Intended | In short |
|---|---|---|
| **OD-007** | **B** | Split `AC-003`: Phase 3 proves replay mechanics, Phase 4 proves read models rebuild identically, Phase 6 proves the Supervisor issues no command |
| **OD-008** | **B** | Transition emission **plus** a periodic refresh inside gate 10's budget · a **gateway epoch** on the record, duplicate identity becomes `(equipmentId, gatewayEpoch, stateSequence)` · retention `compact` only |
| **OD-009** | **A** | Phase 3 owns a shared consume-validate-DLQ component, proven against a test consumer |

## What to challenge

Do not confirm them because they were recommended. Each was recommended by Claude, and Phase 2 has
two cases — `OD-005`'s premise and the `DoR-4` options — where a Claude recommendation was wrong on
the facts and your challenge is what caught it.

### OD-007 = B

1. Is "replay mechanics" actually provable in Phase 3 without a projector, or does the split just
   move the same hole into a smaller box? Name what a Phase 3 test would assert.
2. Does splitting one criterion into three weaken the **F11** guarantee — replay must never move
   equipment — by letting each phase pass its own third while nothing owns the whole?
3. Identifiers: new `AC-nnn` numbers, or lettered parts of `AC-003`? `ac_traceability.mjs` parses
   `^-\s+\*\*(AC-\d{3})` and `TEST_SPECIFICATIONS.md` §7a lists pending ACs by phase. Which choice
   keeps that check honest?

### OD-008 = B

4. The **refresh interval** is unset. Gate 10 fails at `> 10 s`. What else constrains it — the
   Supervisor's startup read, `compact` segment behaviour, `LOAD-002`'s 250 equipment, the freshness
   budget in `TIME_AND_DATA_QUALITY.md`? Give the constraint, not a number you like.
5. **`gatewayEpoch` in the duplicate identity.** The compaction key stays `equipmentId`, so
   compaction is unaffected — is that right? Does any consumer today key on
   `(equipmentId, stateSequence)` in a way that a third field breaks? Is the epoch's *form*
   (start timestamp, random, persisted counter) constrained by anything, or free?
6. **`compact` only, no `delete`.** What is the cost nobody has named: decommissioned equipment
   never ages out, so does the contract now need a **tombstone** rule, and who writes it? Is there a
   case where `delete` was load-bearing and removing it loses something?
7. Is there a fourth hole in this stream that the three found so far are hiding — for example what
   `occurredAtUtc` means on a refresh record that reports **no change**, and whether gate 10 should
   measure that or the ingest clock.

### OD-009 = A

8. A shared component before a single consumer exists is the shape of speculative abstraction. What
   makes this different from the over-building this repository otherwise refuses — or is B honestly
   better, with `AC-027` moving to Phase 4?
9. If A: what exactly must the Phase 3 test consumer do for `AC-027` to be *proven* rather than
   demonstrated? Is a test-only consumer enough evidence for a criterion about production consumers?

### Anything else

10. Do these three decisions, taken together, contradict anything already accepted — `ADR-0017`
    (contracts authoritative), `ADR-0019` (authorization via compacted topic, which has the same
    compaction question), `DEC-007`, or the Phase 2 egress port boundary?

## Output format

`ID · severity (P0/P1/P2/P3) · file:line · what is wrong with the intended decision or what it leaves
unspecified · why it matters · what must change or be fixed numerically`, then one line per decision:
`OD-007: SOUND | SOUND_WITH_CONDITIONS | WRONG — <one sentence>`, and the same for OD-008 and OD-009.

The product owner decides. Do not soften a finding because a decision has been announced.
