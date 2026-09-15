# Instructions for Codex and AGENTS.md-aware coding agents

Before modifying code or creating implementation files, read `MASTER_SPEC.md` and follow the reading order in `README.md`.

## Implementation gate — CHECK THIS FIRST, EVERY SESSION

**Specification status: v0.3. Human decision: APPROVED_WITH_CONDITIONS — all conditions satisfied
(2026-09-15). Gate: OPEN. Phase 1: IN PROGRESS — equipment simulator core implemented 2026-09-15.**

Before doing ANY implementation work, read
`docs/10-human-review/v0.3/HUMAN_APPROVAL.md`.

- Status `PENDING`, `REJECTED`, or `APPROVED_WITH_CONDITIONS` whose conditions are **unstated or
  unmet** → documentation-only work. No application code, no scaffolding, no dependency
  installation, no executable manifests.
- Status `APPROVED` or `APPROVED_WITH_CONDITIONS` → Phase 1 may begin, subject to any recorded
  conditions.

**Do not infer approval.** Not from a new session, not from elapsed time, not from a general
instruction to "continue", and not from agreement between AI agents. Claude and Codex converging on
"Implementation Ready" is **not** authorization — that status is precisely what the human is being
asked to approve. Only the human sets that file.

All four decisions are resolved and **all conditions are satisfied**. The gate is **open**.

**Authorized is not the same as started.** The approval removes a prohibition; it does not issue a
task. Do **not** begin writing application code because this file says the gate is open — begin only
when the product owner asks for specific work.

The product owner asked for Phase 1 on **2026-09-15**. The equipment simulator domain core is
implemented in `src/dotnet/EquipmentSimulator/` and proves **AC-018, AC-019, AC-020** plus
**PROP-03**. `AC-001` and `AC-002` moved to Phase 2 because they require the Edge Gateway. The OPC UA
and Modbus **server endpoints are Phase 2** and need an ADR selecting the libraries before any
dependency is added. This authorization does **not** extend to Phase 2 — ask first.

Every change still follows `DEFINITION_OF_READY.md` and `DUAL_AGENT_PROTOCOL.md`: documentation and
contracts first, Codex challenge where the mandatory list applies, tests mapped to AC IDs.

## Verification you can run at any time

```bash
node tests/contract/validate_examples.mjs   # documented examples vs JSON Schemas
node tests/contract/consistency_check.mjs   # cross-document contradiction checks
node tests/contract/safety_invariants.mjs   # SC-01..SC-10 safety config invariants
```

## Non-negotiable workflow
1. Identify requirement IDs affected by the requested change.
2. Update documentation first.
3. Create/update an ADR when the change introduces a technology, boundary, consistency, reliability, security, or deployment decision.
4. Update contracts and acceptance criteria before code.
5. Produce an implementation plan referencing requirement/ADR IDs.
6. Implement only after the documentation gate is satisfied.
7. Add/update automated tests mapped to acceptance criteria.
8. Run the documented verification commands.
9. Report any divergence; never silently change behavior.

## Forbidden behaviors
- inventing business rules absent from documentation;
- letting AI inference call equipment directly;
- bypassing Safety Supervisor for AI-originated control;
- adding a dependency without documented rationale;
- changing an event/API schema without version/compatibility analysis;
- claiming performance figures not produced by a reproducible test;
- calling this system SIL/functional-safety certified.

If a required decision is missing, create a `DOC-BLOCKER` entry in the change proposal and stop code changes until resolved.
