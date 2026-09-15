# ADR-0015 — OOD is a hard reject; one canonical safety gate table
Status: Accepted (v0.3, dual-agent reviewed)
Decision Record: [DEC-005-ood-semantics](../09-decisions/DEC-005-ood-semantics.md)

## Context
`MASTER_SPEC.md` stated that any failed gate rejects the recommendation, while `FAILURE_MODEL.md` allowed OOD to 'reject or reduce AI authority per model policy'. Separately, two normative gate lists existed (9 gates vs 10, in different orders) and `REASON_CODES.md` implemented the union of both, matching neither.

## Decision drivers
- A safety-critical path cannot carry two semantics.
- Two normative copies of one list will always drift.
- Graded trust cannot be exhaustively tested at portfolio scale.

## Considered options
1. Graded/reduced authority on OOD.
2. Synchronise the two gate lists.
3. Hard reject plus a single canonical table.

## Decision
OOD is a hard reject. `docs/04-ai/AI_SAFETY_AND_MLOPS.md` §2 becomes the single normative gate table (13 gates, canonical order); `MASTER_SPEC.md` §7 is replaced by a reference to it plus the invariants that hold regardless of the table.

## Rationale
Option 3. Option 1 introduces a second, partially-trusted acceptance path that roughly doubles the safety state space and needs its own thresholds and test matrix. Option 2 was rejected because the root cause was not disagreement between the lists but the existence of two normative copies - synchronising them leaves the defect latent. Codex identified this correctly and proposed the reference approach.

## Trade-offs
Loses a potential nuance (partial trust on mildly OOD input), recorded as deliberately deferred and available later by ADR with measured evidence.

## Reliability impact
One decidable acceptance path. Every rejection has exactly one meaning.

## Failure impact
F18 in `FAILURE_MODEL.md`. `OOD_HIGH` always means fallback, never reduced authority.

## Operational impact
Operators see one consistent behaviour for OOD across the dashboard, metrics, and audit records.

## Security impact
None.

## Alternatives rejected
Graded authority was rejected on testability grounds, not on principle, and is recorded as a deferred option rather than a closed door.
