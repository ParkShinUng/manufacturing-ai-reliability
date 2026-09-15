# DEC-004 — Manual / operator-originated command origin

## Status
CONVERGED (3 rounds) — **Claude's proposal withdrawn; Codex's simpler option adopted.**
Human review **APPROVED** (HD-002, 2026-09-15).

## Problem
`API_AND_COMMAND_CONTRACTS.md` permitted "explicit human/manual mode" to originate a production
command. Nothing defined that mode: no state, no authorization, no interlock, no relationship to the
Safety Supervisor. As written it was a **documented bypass of the entire deterministic safety path**,
sitting in plain sight in a contract document. Found by Codex only.

## Relevant requirements
FR-030, FR-035, NFR-006, NFR-012, MASTER_SPEC principle 10. Gap GAP-026.

## Related ADRs
ADR-0002. New: ADR-0014.

## Claude initial proposal (Round 1)
Define `MANUAL_OPERATOR` as a fifth first-class control mode with authentication, full AI suspension,
bounds still enforced, audit, a dead-man idle timeout, and no override of `STOP_REQUIRED`.
Contract in v0.3, implement at P1.

## Codex challenge (Round 1) — **REJECT**
"Defining `MANUAL_OPERATOR` now but implementing it P1 is spec bloat and creates a fifth control mode
while the four existing modes still lack owner/transition/race semantics. The only baseline problem
is one sentence. A documented-but-unimplemented bypass may be mistaken as approved architecture and
affects auth, audit, mode transitions, dashboard state, and `STOP_REQUIRED` semantics."
Simpler alternative: delete the sentence; record manual control as Deferred with **no contract
surface**.

## Claude response (Round 2)
**Classification: ACCEPTED — proposal withdrawn.**
Codex is right on the merits and on the project's own values. Adding a fifth mode while the existing
four were undefined inverts the correct order of work, and MASTER_SPEC principle 10 plus NFR-012
explicitly demand scope discipline. A documented-but-unbuilt bypass is a liability precisely because
it *looks* approved.

**The hole is closed by removal, not by specification** — the smaller and safer change.

## Codex re-evaluation (Round 2) — STILL_OPEN
"`API_AND_COMMAND_CONTRACTS.md` still allows 'explicit human/manual mode'. `OPEN_DECISIONS.md` has no
deferred manual-control entry."

## Claude resolution (Round 3) — ACCEPTED
Correct — claimed but not yet executed. Now done:
- `API_AND_COMMAND_CONTRACTS.md` rewritten: the Safety Supervisor is the **only** production command
  origin in the v0.3 baseline; the manual clause is gone.
- `OPEN_DECISIONS.md` entry 10 records manual control as explicitly **Deferred**, with no contract
  surface, re-introducible only through the documentation-first workflow with its own ADR, state
  machine, authorization model, and acceptance criteria.
- `control.proto` `ControlMode` enum carries an explicit comment that `MANUAL_OPERATOR` is
  deliberately excluded.

## Alternatives considered
| Option | Verdict |
|---|---|
| Fully specify manual mode now | rejected — spec bloat while 4 modes were undefined |
| Specify now, implement P1 | rejected — an unimplemented bypass reads as approved architecture |
| **Delete from baseline; record as Deferred** | **adopted** |

## Evidence
The v0.2 sentence created a second command origin with no interlock. Removal eliminates the class of
problem; specification would only have bounded it.

## Trade-offs
No operator can command equipment directly in the v0.3 baseline. For a portfolio platform
demonstrating AI safety this is defensible and arguably stronger, but it is a **product** decision.

## Final decision
Manual/operator command origin removed from the v0.3 baseline and recorded as Deferred.

## Consensus
Claude: **ACCEPT** · Codex: **ACCEPT** (Round 3)

## Human review

**APPROVED (2026-09-15).** HD-002 resolved: **keep the manual command origin removed** from the
baseline. Recorded in `docs/10-human-review/v0.3/HUMAN_APPROVAL.md`.

Scope note: v0.3 is approved. Manual control remains Deferred with no contract surface;
re-introducing it requires its own ADR, state machine, authorization model, and AC set.

## Related ADR
ADR-0014.
