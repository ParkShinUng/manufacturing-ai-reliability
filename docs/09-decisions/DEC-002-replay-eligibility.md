# DEC-002 — Safety Supervisor replay eligibility

## Status
CONVERGED (3 rounds) — Human review not required

## Problem
The Supervisor consumes predictions from Kafka. On replay it re-evaluates historical predictions,
mints a **new** `decisionId`, and therefore a **new** idempotency key — so the command applies again.
AC-003 claims replay causes no duplicate command effect; the v0.2 design did not deliver that, and
`FAIL-KAFKA-001` is an advertised demonstration.

## Relevant requirements
FR-011, AC-003, NFR-004. Gaps GAP-027, GAP-050.

## Related ADRs
ADR-0001. New: ADR-0012.

## Claude initial proposal (Round 1)
Three independent mechanisms: Supervisor group at `latest`; TTL evaluated against wall clock;
Control Service rejects commands whose source prediction exceeds TTL.

## Codex challenge (Round 1) — ACCEPT_WITH_MODIFICATION
1. Wall-clock TTL depends on a clock model the backlog itself says is missing (GAP-060).
2. **Restart catch-up**: "Supervisor restarts after 12 s, resumes at last committed offset, consumes
   predictions produced during downtime, rejects all by wall-clock TTL; fallback is induced by
   restart length rather than data validity."

## Claude response (Round 2)
**Classification: ACCEPTED** on both.
- Clock model made a **prerequisite**: `docs/02-architecture/TIME_AND_DATA_QUALITY.md` defines clock
  source per field, a ±250 ms skew budget, and mandates monotonic clocks for intervals. The TTL rule
  cites it.
- Codex's finding exposed a worse mechanism than the proposal admitted: `auto.offset.reset=latest`
  applies **only to a group with no committed offset**, so a restarting Supervisor grinds through its
  backlog. Resolution: explicit **`seekToEnd`** on `factory.predictions.v1` at startup, documented as
  *intended* behaviour rather than emergent.

## Codex re-evaluation (Round 2) — STILL_OPEN
"Claude claims explicit `seekToEnd`, but `KAFKA_TOPOLOGY_AND_SEMANTICS.md` §6 still says consumer
restart resumes from last committed offset."

## Claude resolution (Round 3) — ACCEPTED
Correct — the response was written before the edit. `KAFKA_TOPOLOGY_AND_SEMANTICS.md` §6 now carries
an explicit per-consumer override row and a new §6.1 specifying the startup procedure, the intended
discard behaviour, and its interaction with the control lease (a restarted Supervisor starts fenced,
unauthorised, and on fallback, and must re-earn AI authority through M5).

## Alternatives considered
| Option | Verdict |
|---|---|
| Derive `decisionId` deterministically from `predictionId` | rejected — a re-decision on the same prediction is legitimately *different* if equipment state changed; a stable ID would suppress a real new decision |
| Forbid replay entirely | rejected — destroys `FAIL-KAFKA-001` |
| **Three mechanisms + seekToEnd** | **adopted** |

## Evidence
Wall-clock TTL alone makes replayed predictions inert *by construction*, requiring no new component —
but only if the comparison basis is normative, which v0.2 never stated.

## Trade-offs
After downtime exceeding the TTL, all predictions from that window are discarded and the equipment
runs on fallback for up to one inference cadence (5 s). Accepted and documented as intended.

## Final decision
Supervisor is not replay-eligible; three mechanisms plus `seekToEnd`.

## Consensus
Claude: **ACCEPT** · Codex: **ACCEPT** (Round 3)

## Human review
NOT_REQUIRED.

## Related ADR
ADR-0012.
