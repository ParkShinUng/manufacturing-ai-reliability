# ADR-0014 — No manual/operator command origin in the v0.3 baseline
Status: Accepted (v0.3, dual-agent reviewed)
Decision Record: [DEC-004-manual-mode](../09-decisions/DEC-004-manual-mode.md)

## Context
`API_AND_COMMAND_CONTRACTS.md` permitted 'explicit human/manual mode' to originate a production command, with no state, authorization, interlock, or relationship to the Safety Supervisor defined anywhere. It was a documented bypass of the deterministic safety path.

## Decision drivers
- MASTER_SPEC principle 10 and NFR-012: scope discipline; components require a documented requirement.
- A documented-but-unimplemented bypass reads as approved architecture.
- The four existing control modes still lacked owners and transitions.

## Considered options
1. Fully specify `MANUAL_OPERATOR` as a fifth control mode now.
2. Specify now, implement at P1.
3. Delete from the baseline; record as Deferred.

## Decision
Remove the manual command origin from the v0.3 baseline. The Safety Supervisor is the only production command origin. Manual operator control is recorded as explicitly Deferred with no contract surface, re-introducible only through the documentation-first workflow with its own ADR, state machine, authorization model, and acceptance criteria.

## Rationale
Option 3. Claude initially proposed option 2; Codex rejected it as spec bloat that would add a fifth mode while the existing four were undefined, and Claude withdrew the proposal. The hole is closed by **removal**, which is both the smaller and the safer change - specification would only have bounded the bypass, not eliminated it.

## Trade-offs
No operator can command equipment directly in the v0.3 baseline. For a platform whose thesis is bounded AI authority this is defensible, but it is a product decision and is escalated to human review as HD-002.

## Reliability impact
Eliminates an entire class of authority-bypass risk rather than bounding it.

## Failure impact
No failure row is needed for a path that does not exist - which is the point.

## Operational impact
Simpler operator training surface; `STOP_REQUIRED` reset (M7) remains the one operator-gated action.

## Security impact
Removes an unauthenticated, unaudited command origin from the contract.

## Alternatives rejected
Specifying manual mode now was rejected because an unimplemented bypass in a contract document is more dangerous than an absent feature.
