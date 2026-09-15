# API and Command Contracts

> **The authoritative contract is `contracts/proto/control/v1/control.proto`** (ADR-0017).
> The shapes below are explanatory and must match it field for field.

## Control command (`ApplyCommandRequest`)

Safety Supervisor creates a command intent; Control Service validates again and executes.

```json
{
  "commandId": "uuid",
  "idempotencyKey": "equipmentId:decisionId",
  "equipmentId": "eq-001",
  "commandType": "SET_OPERATION_RATE",
  "target": 70.0,
  "unit": "PERCENT",
  "controlEpoch": 42,
  "issuedAt": "2026-09-15T04:00:05.180Z",
  "expiresAt": "2026-09-15T04:00:07.180Z",
  "sourcePredictionAt": "2026-09-15T04:00:05.000Z",
  "decisionId": "uuid",
  "correlationId": "uuid",
  "causationId": "uuid"
}
```

**There is deliberately no `source` field.** In v0.2 it was a self-asserted string, so any process
able to reach the Control Service port could claim to be the Safety Supervisor, defeating ADR-0002.
Origin is derived server-side from the verified mTLS peer identity (ADR-0016).

`expiresAt` MUST equal `issuedAt + 2000 ms`, shorter than the 5 s inference cadence. That inequality
is the structural guarantee behind ADR-0013, not a tuning value.

## Required response (`ApplyCommandResponse`)

```json
{
  "commandId": "uuid",
  "status": "APPLIED|REJECTED|EXPIRED|DUPLICATE|FAILED|SUPERSEDED|FENCED",
  "appliedAt": "2026-09-15T04:00:05.290Z",
  "reasonCode": "COMMAND_APPLIED",
  "authenticatedSource": "spiffe://mair/safety-supervisor",
  "controlEpoch": 42,
  "resultingMode": "AI_ASSISTED",
  "appliedTarget": 90.0,
  "correlationId": "uuid"
}
```

`authenticatedSource` is server-derived and never echoed from the request; it preserves the audit
chain (NFR-002) after `source` was removed.

## Rules
- Control Service rejects expired commands, superseded commands, and commands carrying a stale `controlEpoch`.
- Control Service clamps/rejects targets outside equipment limits.
- Duplicate idempotency keys return prior result without repeating effect.
- **The Safety Supervisor is the ONLY production command origin in the v0.3 baseline.** The v0.2 allowance for "explicit human/manual mode" has been **removed** (DEC-004): it was a second command origin with no defined state, authorization, interlock, or relationship to the Supervisor, and therefore a documented bypass of the deterministic safety path. Operator/manual control is recorded as explicitly **Deferred** in `docs/08-roadmap/OPEN_DECISIONS.md`.
- Command origin is **authenticated, never self-asserted**. The `source` request field has been removed from the contract; origin is derived server-side from the verified mTLS peer identity and returned as `authenticatedSource` (DEC-006).
- Simulator fault-injection commands are **simulator-admin writes**, not equipment-control writes, and exist only in the demo profile. They never traverse the Control Service and never move a production setpoint.
