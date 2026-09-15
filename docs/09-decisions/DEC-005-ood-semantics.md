# DEC-005 — OOD: hard reject vs reduced authority

## Status
CONVERGED — Human review not required

## Problem
`MASTER_SPEC.md` §7: any failed gate rejects the recommendation.
`FAILURE_MODEL.md`: OOD may "reject **or reduce AI authority** per model policy."
Two different safety semantics on the same path. Found by Codex only.

Compounding it, the two normative gate lists disagreed: MASTER_SPEC had 9 gates,
`AI_SAFETY_AND_MLOPS.md` had 10 in a different order, and `REASON_CODES.md` implemented the **union**
of both, matching neither.

## Relevant requirements
FR-031, FR-033, AC-005. Gaps GAP-020, GAP-025.

## Related ADRs
ADR-0002. New: ADR-0015.

## Claude initial proposal (Round 1)
OOD is a hard reject; delete "or reduce AI authority".

## Codex challenge (Round 1) — ACCEPT_WITH_MODIFICATION
"Hard reject is the simpler choice and aligns with MASTER_SPEC §7, but the proposal only edits one
contradiction and does not reconcile the 9-gate vs 10-gate mismatch. It closes OOD semantics but not
the gate-list authority problem." Simpler alternative: MASTER_SPEC should **reference** one canonical
gate table rather than carry a second copy.

## Claude response (Round 2)
**Classification: ACCEPTED.** Codex identified the root cause correctly: the problem was not that the
two lists disagreed but that **two normative copies existed at all**. Synchronising them would have
left the same defect latent.

`docs/04-ai/AI_SAFETY_AND_MLOPS.md` §2 becomes the single normative gate table (13 gates, canonical
order). `MASTER_SPEC.md` §7 is replaced by a reference plus the invariants that hold regardless of
the table. `REASON_CODES.md` derives from it.

## Codex re-evaluation (Round 2) — STILL_OPEN
"MASTER_SPEC §7 still has 9 gates, `AI_SAFETY_AND_MLOPS.md` still has 10, and `FAILURE_MODEL.md`
still says OOD may 'reject or reduce AI authority'."

## Claude resolution (Round 3) — ACCEPTED
Executed. MASTER_SPEC §7 now references the canonical table; `AI_SAFETY_AND_MLOPS.md` §2 carries the
13-gate table with §2.1 recording the OOD decision; `FAILURE_MODEL.md` §5 carries an explicit
"Removed in v0.3" note so the deleted semantics cannot silently return.

## Alternatives considered
| Option | Verdict |
|---|---|
| Graded/reduced authority on OOD | **deferred** — a second partially-trusted acceptance path roughly doubles the safety state space, needs its own thresholds and test matrix, and cannot be exhaustively tested at portfolio scale |
| Synchronise the two gate lists | rejected — leaves two normative copies, so the defect recurs |
| **Hard reject + single canonical table** | **adopted** |

## Evidence
`REASON_CODES.md` already implemented the union of both lists, which is direct evidence that neither
list was being treated as authoritative in practice.

## Trade-offs
Loses a potential nuance (partial trust on mildly OOD input). Recorded as deliberately deferred,
available later by ADR with measured evidence.

## Final decision
OOD is a hard reject. One canonical gate table in `AI_SAFETY_AND_MLOPS.md` §2.

## Consensus
Claude: **ACCEPT** · Codex: **ACCEPT** (Round 3)

## Human review
NOT_REQUIRED.

## Related ADR
ADR-0015.
