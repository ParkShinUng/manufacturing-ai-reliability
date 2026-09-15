# Human Approval Record — Specification v0.3

## Status

**APPROVED_WITH_CONDITIONS**

All conditions are satisfied (see below). Phase 1 is authorized; implementation has not started.

> The status line above carries **only** the enum value, so it stays machine-parseable. Qualifiers
> belong in prose, not in the status field - the consistency checker rejects a status that is not
> exactly one of the five allowed values, which is what caught an earlier attempt to write
> "APPROVED_WITH_CONDITIONS - all conditions satisfied" as the status itself.

## Allowed values

`PENDING` · `APPROVED` · `APPROVED_WITH_CONDITIONS` · `REQUEST_CHANGES` · `REJECTED`

> This file is set **only by the human product owner**. No AI agent may change this status.
> Claude Code recorded the values below as dictated by the product owner on 2026-09-15 and must not
> alter them.

## Meaning

| Status | Effect |
|---|---|
| `PENDING` | Implementation must not start. Documentation-only work permitted. |
| `APPROVED` | Phase 1 may begin. |
| **`APPROVED_WITH_CONDITIONS`** | **Phase 1 may begin once the recorded conditions are satisfied.** |
| `REQUEST_CHANGES` | Return to specification work; v0.3 is not frozen. |
| `REJECTED` | Stop. Revisit product direction. |

## Decisions recorded

| ID | Question | Answer | Date |
|---|---|---|---|
| **HD-001** | Max authorization staleness when Kafka is unreachable | **APPROVED** — 60 s | 2026-09-15 |
| **HD-002** | Manual/operator command origin | **APPROVED** — keep removed | 2026-09-15 |
| **HD-003** | Ratify ADR-0011 control authority change | **APPROVED** — ratified | 2026-09-15 |
| **HD-004** | Overall v0.3 approval | **APPROVED_WITH_CONDITIONS** | 2026-09-15 |

> **Revision note.** HD-004 was first recorded as `REJECTED` on 2026-09-15 and changed to
> `APPROVED_WITH_CONDITIONS` by the product owner the same day. The interim state is noted here
> rather than erased, because an approval record that quietly rewrites its own history is not an
> audit trail.

## Conditions

**Stated 2026-09-15. The conditions are the three architecture decisions put to the product owner.**

| # | Condition | State | Evidence |
|---|---|---|---|
| C-1 | **HD-001 resolved** — maximum authorization staleness decided | ✅ **SATISFIED** | 60 s. `SAFETY_CONFIGURATION.md`, `safety-config.schema.json`, `safety-config.demo.json` all reconciled; no pending markers remain. |
| C-2 | **HD-002 resolved** — manual/operator command origin decided | ✅ **SATISFIED** | Keep removed. DEC-004 / ADR-0014 human-review sections closed; `OPEN_DECISIONS.md` #10 records it as Deferred with no contract surface. |
| C-3 | **HD-003 resolved** — ADR-0011 control authority change ratified | ✅ **SATISFIED** | Ratified. DEC-001 / ADR-0011 human-review sections closed. |

**All conditions satisfied. Phase 1 is authorized.**

## Record

| Field | Value |
|---|---|
| Reviewed by | Product owner |
| Date | 2026-09-15 |
| Status | **APPROVED_WITH_CONDITIONS** |
| Conditions | C-1, C-2, C-3 — **all satisfied** |
| Implementation | **Authorized, not started** |
| Notes | Specification review closed with 0 unresolved P0 and P1 after 3 dual-agent rounds. |

## What this status means in practice

| | |
|---|---|
| v0.3 specification | **Approved** |
| The three architecture decisions | **Approved**; DEC-001/004/009 and ADR-0011/0014/0019 human-review sections closed |
| Conditions | **All satisfied** |
| **Phase 1** | **Authorized** |
| **Implementation** | **NOT STARTED** |

### Authorized is not the same as started

Phase 1 does **not** begin automatically. It begins when the product owner asks for it. An agent
reading this file must not treat "authorized" as an instruction to start writing code — the approval
removes the prohibition; it does not issue a task.

The first Phase 1 work, when requested, is
[`docs/11-service-design/EQUIPMENT_SIMULATOR.md`](../../11-service-design/EQUIPMENT_SIMULATOR.md),
proving AC-001, AC-002, AC-018, AC-019, AC-020.

### What remains true regardless of this approval

- Every performance figure in this repository is still `TARGET (unmeasured)`. Approval does not make
  a target a result; only a report under `reports/` does (NFR-011, AC-042).
- AC-011 and AC-012 — the tests that prove the headline reliability claim — cannot run until
  **Phase 7** delivers the Control Service watchdog. ADR-0011 is ratified but unproven until then.
- The documentation-first workflow still applies to every change
  (`DEFINITION_OF_READY.md`, `DUAL_AGENT_PROTOCOL.md`).

## Silence is not approval

A recorded status never widens on its own. `APPROVED_WITH_CONDITIONS` did not become `APPROVED`
because time passed; it is satisfied because the three named conditions were each met and the
evidence is recorded above. Claude Code must re-read this file at the start of any later session
before doing implementation work, and must not interpret elapsed time, a new session, or a general
instruction to continue as a widening of scope.
