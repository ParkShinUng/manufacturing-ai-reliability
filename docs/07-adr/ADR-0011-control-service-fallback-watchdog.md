# ADR-0011 — Control Service fallback watchdog with epoch fencing
Status: Accepted (v0.3, dual-agent reviewed)
Decision Record: [DEC-001-fallback-authority](../09-decisions/DEC-001-fallback-authority.md)

## Context
The Safety Supervisor was the only command origin, and `SAFE_FALLBACK` was a mode of the Supervisor rather than a property of the system. Supervisor death left equipment holding a possibly AI-elevated setpoint indefinitely, so MASTER_SPEC principle 3 held only for inference failure.

## Decision drivers
- NFR-001: AI unavailability must not remove deterministic fallback capability.
- FR-035: a single application owner of equipment writes.
- The claim must be true for *any* component's death, not only the AI's.

## Considered options
1. Control Service watchdog only.
2. Equipment dead-man only.
3. Do nothing; document that equipment holds its last rate.
4. Watchdog + dead-man + epoch fencing.

## Decision
Adopt a three-layer authority model. Control Service (L2) runs a fallback watchdog that drives equipment to the configured fallback rate after 12 s of Supervisor silence, and owns a durable strictly-monotonic `controlEpoch` per equipment that fences commands issued under a superseded lease. The simulated equipment (L3) holds an independent 30 s dead-man revert.

## Rationale
Option 4. Options 1 and 2 each leave a gap: 1 does not survive Control Service death, 2 produces no application-level audit trail and reacts slowly. Option 3 abandons the platform's headline reliability claim. Epoch fencing was added after Codex demonstrated that a watchdog without it is defeated by a delayed command: the watchdog fires at T+12 s, and a command issued at T0 but delayed in the network arrives still unexpired and moves the equipment back up.

## Trade-offs
Adds durable per-equipment state (epoch, mode, budget) to Control Service and a lease handshake to the Supervisor. Accepted, because the alternative is an unfenced control path.

## Reliability impact
Removes the Safety Supervisor as a single point of failure for safe-direction movement. Three independent layers must fail before equipment is stranded at an unsupervised setpoint.

## Failure impact
F23 (Supervisor unavailable), F24 (late command after watchdog), F25 (Control Service unavailable), F30 (network partition) in `FAILURE_MODEL.md`.

## Operational impact
Operators see an explicit `SUPERVISOR_SILENT` outcome and a mode change rather than silent drift. `watchdog_fallback_total` is a first-class metric.

## Security impact
The epoch is not a security control; command origin authenticity is ADR-0016. The two compose: an authenticated caller can still be fenced.

## Alternatives rejected
Doing nothing was rejected because it would have left a documented reliability claim that the architecture does not support - the single most likely finding for a reviewer to make.
