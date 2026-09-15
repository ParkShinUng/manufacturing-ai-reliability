# DEC-001 — Fallback authority when the Safety Supervisor dies

## Status
CONVERGED (3 rounds) — Human review **APPROVED** (HD-003, 2026-09-15)

## Problem
The Safety Supervisor was the only component that issues commands, and `SAFE_FALLBACK` was a *mode of
the Supervisor* rather than a property of the system. Killing the Python inference service left the
Supervisor alive to reject AI and command fallback, so AC-004 passed. Killing the **Supervisor** left
nothing commanding anything: equipment holds its last commanded rate indefinitely, possibly an
AI-elevated one. MASTER_SPEC principle 3 ("Control survives AI failure") was therefore only true for
inference failure.

## Relevant requirements
NFR-001, FR-023, FR-033, FR-035, AC-004. Gaps GAP-021, GAP-022.

## Related ADRs
ADR-0002 (AI not direct control), ADR-0009 (gRPC command path). New: ADR-0011.

## Claude initial proposal (Round 1)
Two deterministic layers containing no AI logic: a Control Service fallback watchdog firing on
Supervisor silence, and a simulator/equipment dead-man timer.

## Codex challenge (Round 1) — **REJECT**
1. Two autonomous writers weakens ADR-0002/FR-035 "sole equipment-write owner".
2. **No fencing of late Supervisor commands.** "Supervisor sends a command at T0; network delays it;
   the watchdog fires at T+15 s and writes 60 %; the delayed command arrives at T+16 s, still
   unexpired, and moves the equipment back up."
3. Split-brain: Supervisor alive but partitioned from Control Service.
4. Timeout values arbitrary and not justified against the documented 5 s cadence / 10 s TTL.

## Claude response (Round 2)
**Classification: ACCEPTED** (objection 2 is the key finding and was a genuine miss),
**PARTIALLY_ACCEPTED** (objection 1).

- **Fencing added.** Control Service owns a durable, strictly monotonic `controlEpoch` per equipment,
  incremented on any autonomous action and on every lease acquisition. Commands carry the epoch the
  Supervisor believes current; a lower epoch is rejected with `COMMAND_EPOCH_STALE`. A fenced
  Supervisor must re-acquire a lease and **re-derive from current telemetry**, never resend the old
  intent. This is the standard fencing-token pattern and closes objections 2 and 3 with one mechanism.
- **Three-layer authority model** stated explicitly (L1 advisory / L2 sole *application* writer /
  L3 equipment self-protection). The dead-man is the equipment protecting itself — the same class as
  an over-temperature trip — not an application writer, so FR-035 holds as written.
- **Timeouts derived:** `supervisor_silence_timeout` = 12 s = prediction TTL (10 s) + one gRPC
  command budget (1.4 s) + margin. Below it the Supervisor could still be acting on a valid
  prediction; above it nothing it sends would pass its own TTL gate. Dead-man 30 s = 2.5× the
  watchdog, so L3 acts only if L2 has also failed.

## Codex re-evaluation (Round 2) — STILL_OPEN
"`controlEpoch` is named but not contracted: no `control_epoch` field, no `AcquireControlLease` RPC,
no lease TTL, no durable persistence rule. Control Service restart can reintroduce the stale-command
race — the exact failure mode Codex correctly identified for DEC-003."

## Claude resolution (Round 3) — ACCEPTED
Fully contracted in `contracts/proto/control/v1/control.proto`: `control_epoch` on request and
response, `AcquireControlLease`, `Heartbeat`, `EquipmentLease` with expiry.
**Durability rule** (`CONTROL_SERVICE.md` §8): the epoch increment is committed to PostgreSQL
**before** the equipment write it authorises. Dying between commit and write leaves the epoch merely
ahead — harmless. The reverse order could re-issue an observed epoch and un-fence a stale command.
On restart the epoch is incremented **once more** before any command is accepted.

## Alternatives considered
| Option | Verdict |
|---|---|
| Control Service watchdog only | rejected — does not survive Control Service death |
| Equipment dead-man only | rejected as sole measure — slow, no application audit trail |
| Do nothing; document that equipment holds last rate | rejected — abandons the headline claim |
| **Watchdog + dead-man + epoch fencing** | **adopted** |

## Evidence
Codex's delayed-command scenario is reproducible in design: 2 s command TTL vs 12 s watchdog leaves a
10 s window in which a delayed command is unexpired but obsolete. Fencing is the only mechanism that
closes it without a synchronised clock.

## Trade-offs
Adds durable per-equipment state to Control Service and a lease handshake. Accepted: the alternative
is an unfenced control path.

## Final decision
Three-layer authority with `controlEpoch` fencing, a 12 s Control Service watchdog, and a 30 s
equipment dead-man.

## Consensus
Claude: **ACCEPT** · Codex: **ACCEPT** (Round 3, after contracting)

## Human review

**APPROVED (2026-09-15).** HD-003 resolved: the control authority change is **ratified**. All three
elements were accepted — the Control Service may write autonomously to the configured fallback rate
on watchdog, the simulated equipment may self-revert after 30 s, and FR-035's wording is scoped to
application components. Recorded in `docs/10-human-review/v0.3/HUMAN_APPROVAL.md`.

Scope note: v0.3 is approved and Phase 1 is authorized, but ADR-0011 is **ratified, not proven**.
AC-011 and AC-012 cannot run until Phase 7 delivers the Control Service watchdog.

## Related ADR
ADR-0011.
