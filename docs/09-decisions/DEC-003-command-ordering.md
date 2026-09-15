# DEC-003 — Command ordering and supersession

## Status
CONVERGED (3 rounds) — Human review not required
**Codex's alternative was adopted over Claude's proposal, twice.**

## Problem
Ordering was decided by arrival. Two unexpired commands arriving out of order both apply, so an
*older* intent can win — incorrect for a setpoint.

## Relevant requirements
FR-034, FR-035, AC-007. Gaps GAP-004, GAP-032.

## Related ADRs
ADR-0009. New: ADR-0013.

## Claude initial proposal (Round 1)
Per-equipment strictly monotonic `intent_sequence` assigned by the Supervisor; Control Service
persists `last_applied_intent_sequence` and rejects `<=`.

## Codex challenge (Round 1) — **REJECT**
"Supervisor applies sequence 900, crashes before persisting 901, restarts at 0, and every command is
rejected as `COMMAND_SUPERSEDED` — permanently. With two Supervisor replicas, both emit 901 for
different predictions." Control Service correctness would depend on Supervisor storage and replica
topology. Codex's alternative: move ordering authority to Control Service, comparing
`source_prediction_at_utc`, avoiding a distributed counter.

## Claude response (Round 2)
**Classification: ACCEPTED — Codex's alternative adopted.** The cure was worse than the disease: a
lost counter bricks the control path. Ordering moved to Control Service comparing
`sourcePredictionAtUtc`, with a new requirement that the prediction service guarantee strictly
increasing `predictedAtUtc` per equipment.

## Codex re-evaluation (Round 2) — **NEW_PROBLEM_INTRODUCED**
"`predictedAtUtc` is produced by the prediction host wall clock. No per-equipment monotonic allocator
survives restart, rebalance, duplicate producer, or a backward clock step. Kafka partitioning orders
records after production; it does not make producer timestamps monotonic." A consumer-group rebalance
can briefly give two instances the same partition.

## Claude resolution (Round 3) — ACCEPTED, design changed again
Codex is right, and the fix removes the reliance on cross-host clock monotonicity entirely:

**Make concurrent validity impossible instead of ordering concurrent commands.**
- Command validity is **2 s**; the inference cadence is **5 s**. Two commands for one equipment can
  therefore never be concurrently valid.
- The Supervisor keeps **at most one outstanding command per equipment** — a purely local invariant
  needing no distributed state.
- `controlEpoch` (DEC-001) fences anything issued under a superseded lease.
- Control Service serialises per equipment key.

`sourcePredictionAtUtc` is retained as a **secondary, defence-in-depth guard** plus audit data — it
is explicitly **not** the primary ordering mechanism.

Codex's Round-3 verification found that the first draft of this record overstated the change: the
contracts still carried a supersession comparison, and the Control Service pseudocode had the
predicate **inverted** (`> lastAccepted -> SUPERSEDED`, which would have rejected every *newer*
command — the exact opposite of the intent). Both are fixed. The honest statement is:

- **Primary (correctness depends on it):** 2 s command TTL < 5 s cadence, one outstanding command per
  equipment, per-equipment serialisation, epoch fencing. No clock, no counter.
- **Secondary (correctness does not depend on it):** reject when
  `sourcePredictionAtUtc <= lastAcceptedSourcePredictionAtUtc`.

The secondary guard is safe to keep precisely because its failure mode is **asymmetric**: a backward
clock step or a rebalance can only make it *reject a valid command*, never *accept a stale one*. A
spurious rejection costs one inference cadence, since the Supervisor re-derives from current
telemetry. Codex's objection — that wall clocks are not monotonic across restart or rebalance — is
correct, and is fatal to a clock used as a *primary* ordering mechanism; it is not fatal to a
fail-safe secondary check.

## Alternatives considered
| Option | Verdict |
|---|---|
| Supervisor-assigned `intent_sequence` | rejected (Codex R1) — lost counter bricks the control path |
| Ordering by `sourcePredictionAtUtc` | rejected (Codex R2) — wall clock is not monotonic across restart/rebalance |
| **TTL shorter than cadence + single outstanding command + epoch fencing** | **adopted** |

## Evidence
2 s TTL < 5 s cadence is a *structural* guarantee, independent of clocks, counters, and replica
topology — the only one of the three options with no distributed failure mode.

## Trade-offs
A command must land within 2 s or be re-derived. Acceptable: a stale setpoint is worth less than a
fresh decision, and the gRPC profile (2 attempts × 500 ms) fits comfortably inside the window.

## Final decision
No distributed counter. Structural non-concurrency + epoch fencing + per-equipment serialisation.

## Consensus
Claude: **ACCEPT** · Codex: **ACCEPT** (Round 3)

## Human review
NOT_REQUIRED.

## Related ADR
ADR-0013.
