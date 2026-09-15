# ADR-0012 — Safety Supervisor is not replay-eligible
Status: Accepted (v0.3, dual-agent reviewed)
Decision Record: [DEC-002-replay-eligibility](../09-decisions/DEC-002-replay-eligibility.md)

## Context
The Supervisor consumes predictions from Kafka. On replay it re-evaluates historical predictions and mints a new `decisionId`, hence a new idempotency key, so the command applies again. AC-003 claimed replay causes no duplicate command effect; the v0.2 design did not deliver it.

## Decision drivers
- AC-003 and `FAIL-KAFKA-001` both require replay to be demonstrable.
- Replay must never move live equipment.
- `auto.offset.reset` alone does not govern a restarting consumer that already has committed offsets.

## Considered options
1. Deterministic `decisionId` derived from `predictionId`.
2. Forbid replay entirely.
3. Three mechanisms plus an explicit startup seek.

## Decision
The Supervisor consumer group is not replay-eligible. Three independent mechanisms enforce it: `auto.offset.reset=latest` with no documented reset procedure; prediction TTL evaluated against wall-clock **now** (normative); and Control Service rejection of commands whose source prediction exceeds the TTL bound. The Supervisor additionally performs an explicit `seekToEnd` on `factory.predictions.v1` at startup.

## Rationale
Option 3. Option 1 is attractive but wrong: a re-decision on the same prediction is legitimately *different* if equipment state changed, so a stable ID would suppress a real new decision. Option 2 destroys a portfolio deliverable. Wall-clock TTL makes replayed predictions inert by construction and needs no new component - but only once the comparison basis is stated normatively, which v0.2 never did.

## Trade-offs
After downtime exceeding the prediction TTL, predictions produced during that window are discarded and equipment runs on fallback for up to one inference cadence (5 s). Documented as intended behaviour.

## Reliability impact
Replay becomes a safe, demonstrable operation on projection and analytics groups while being structurally inert for the control path.

## Failure impact
F11 in `FAILURE_MODEL.md`.

## Operational impact
Replay runbooks target projector groups only. The Supervisor has no documented offset-reset procedure, which is itself the control.

## Security impact
None directly; reduces the blast radius of an operator error.

## Alternatives rejected
Forbidding replay entirely was rejected as it removes an advertised reliability demonstration for a problem solvable by three cheap mechanisms.
