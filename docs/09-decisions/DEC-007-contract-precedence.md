# DEC-007 — Contract precedence and event envelope

## Status
CONVERGED — Human review not required
**The only decision where Claude retained a partial divergence from Codex, with evidence.**

## Problem
`REPOSITORY_STRUCTURE.md` declared `contracts/` the machine-readable source of truth and forbade
hand-edited DTO divergence. `MASTER_SPEC.md` §9 ranked `docs/03-contracts` **above** it. The two had
already diverged in practice: the telemetry example in prose omitted three schema-required fields
(`voltageV`, `torqueNm`, `operationRatePct`) and would fail validation against its own schema.

Separately, event metadata was inconsistent: telemetry carried `eventId`/`schemaVersion`/`sequence`,
predictions carried none of them, and no event carried `eventType` or `causationId`.

## Relevant requirements
NFR-007, NFR-008. Gaps GAP-001, GAP-003, GAP-010.

## Related ADRs
ADR-0006. New: ADR-0017.

## Claude initial proposal (Round 1)
`contracts/` authoritative; prose explanatory with every example a validated instance; plus a common
envelope including `sequence` on every event.

## Codex challenge (Round 1) — ACCEPT_WITH_MODIFICATION
"Making `contracts/` authoritative is consistent, but a mandatory common envelope with `sequence` for
every Kafka event imports unresolved sequence semantics (GAP-051) into all contracts. It may overfit
telemetry ordering needs onto prediction/model-deployment/fault events." Risk: "global-looking
`sequence` fields with no reset/scope rules will create false ordering assumptions across producers."
Restart scenario: "producer restart resets a per-process sequence to 0 and consumers treat valid new
events as duplicates or gaps."

## Claude response (Round 2)
**Classification: PARTIALLY_ACCEPTED.**

**Accepted — `sequence` removed from the envelope.** Codex is right: a universal sequence field
invites exactly the false cross-producer ordering assumption it warns about. `sequence` now appears
only where a consumer genuinely needs gap/order detection, each with an explicit scope:
- `factory.telemetry.v1` → `sequence`, per equipment, **assigned by the equipment** (so gateway-side
  loss is detectable — a gateway-assigned sequence would be continuous even when samples were dropped);
- `factory.equipment-states.v1` → `stateSequence`, per equipment, **assigned by the gateway**, named
  distinctly so the two can never be confused.

**Retained with evidence — `producer` stays in the envelope.** Codex proposed a minimal envelope.
`producer` is kept because the `causationId` chain cannot be audited across services without knowing
which service emitted each link, and NFR-002 requires exactly that traceability. This is the single
point in the nine decisions where Claude did not fully adopt Codex's position.

Final envelope: `eventId, eventType, schemaVersion, equipmentId?, occurredAtUtc, ingestTimeUtc,
correlationId, causationId, producer`.

## Codex re-evaluation (Round 2) — STILL_OPEN
"`EVENT_CONTRACTS.md` still has non-envelope event examples and `contracts/` still contains only
telemetry. Contract precedence remains theoretical without schemas for
prediction/decision/outcome/state/model-deployment."

## Claude resolution (Round 3) — ACCEPTED
Executed. Seven schemas now exist: `envelope`, `telemetry`, `prediction`, `safety-decision`,
`control-outcome`, `equipment-state`, `model-authorization`. `MASTER_SPEC.md` §9 places
machine-readable contracts **above** contract prose. `EVENT_CONTRACTS.md` is rewritten to reference
the schemas, and every example it carries is a validated instance enforced by a contract test.

## Alternatives considered
| Option | Verdict |
|---|---|
| Prose authoritative | rejected — prose cannot be machine-validated, and had already drifted |
| Envelope with universal `sequence` | rejected (Codex) — false ordering assumptions |
| **`contracts/` authoritative + minimal envelope + scoped sequence** | **adopted** |

## Evidence
The v0.2 telemetry example failing its own schema is direct evidence that prose drifts and schemas do
not. A contract test now makes recurrence impossible.

## Trade-offs
Prose documents must be regenerated or re-validated when schemas change. Accepted — that is the
enforcement mechanism, not a cost.

## Final decision
Machine-readable contracts authoritative; minimal envelope; `sequence` scoped per event type;
`producer` retained.

## Consensus
Claude: **ACCEPT** · Codex: **ACCEPT** (Round 3, with the `producer` divergence noted and accepted)

## Human review
NOT_REQUIRED.

## Related ADR
ADR-0017.
