# ADR-0017 — Machine-readable contracts outrank contract prose; minimal event envelope
Status: Accepted (v0.3, dual-agent reviewed)
Decision Record: [DEC-007-contract-precedence](../09-decisions/DEC-007-contract-precedence.md)

## Context
`REPOSITORY_STRUCTURE.md` called `contracts/` the machine-readable source of truth while `MASTER_SPEC.md` §9 ranked `docs/03-contracts` above it - and the two had already diverged: the telemetry example in prose omitted three schema-required fields and would fail validation against its own schema. Event metadata was also inconsistent across event types.

## Decision drivers
- NFR-007: public contracts independent of implementation types.
- NFR-008: contract-breaking changes require explicit versioning.
- Prose drifts; schemas can be validated.

## Considered options
1. Prose authoritative.
2. Envelope including a universal `sequence`.
3. `contracts/` authoritative with a minimal envelope and per-event-type scoped sequence.

## Decision
Machine-readable contracts in `contracts/` are authoritative over contract prose. Prose becomes explanatory and every example it contains must be a validated instance, enforced by a contract test. Adopt a minimal common envelope: `eventId, eventType, schemaVersion, equipmentId?, occurredAtUtc, ingestTimeUtc, correlationId, causationId, producer`. `sequence` is **not** in the envelope.

## Rationale
Option 3. Option 1 cannot be machine-validated and had already drifted in practice. Option 2 was rejected after Codex observed that a universal `sequence` imports telemetry's ordering semantics onto events that have no such need and invites false cross-producer ordering assumptions. `sequence` therefore appears only on telemetry (equipment-assigned, so gateway-side loss is detectable) and as `stateSequence` on equipment states (gateway-assigned, distinctly named).

## Trade-offs
Prose must be re-validated when schemas change. That is the enforcement mechanism rather than a cost. `producer` was retained in the envelope against Codex's minimalism, with evidence: the `causationId` chain cannot be audited across services without it, and NFR-002 requires that traceability.

## Reliability impact
Seven schemas now exist where one did. Contract tests make prose/schema drift impossible to reintroduce silently.

## Failure impact
Prevents the class of defect where a producer implemented from a documented example emits events the schema rejects.

## Operational impact
Contract tests run in CI; a failing example is a build failure.

## Security impact
None directly.

## Alternatives rejected
Prose authority was rejected on evidence: the v0.2 example failing its own schema demonstrates that unvalidatable prose drifts.
