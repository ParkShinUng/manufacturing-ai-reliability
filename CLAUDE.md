# Claude Code project instructions

This file intentionally mirrors the platform-neutral rules in `AGENTS.md`.

Read `MASTER_SPEC.md` and the full reading order in `README.md` before implementation.

## Implementation gate — CHECK THIS FIRST, EVERY SESSION

**Specification status: v0.3. Human decision: APPROVED_WITH_CONDITIONS — all conditions satisfied
(2026-09-15). Gate: OPEN. Phase 1 authorized. Implementation: NOT STARTED.**

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
when the product owner asks for specific work. When they do, Phase 1 is
`docs/11-service-design/EQUIPMENT_SIMULATOR.md` (AC-001, AC-002, AC-018, AC-019, AC-020).

Every change still follows `DEFINITION_OF_READY.md` and `DUAL_AGENT_PROTOCOL.md`: documentation and
contracts first, Codex challenge where the mandatory list applies, tests mapped to AC IDs.

## Verification you can run at any time

```bash
node tests/contract/validate_examples.mjs   # documented examples vs JSON Schemas
node tests/contract/consistency_check.mjs   # cross-document contradiction checks
node tests/contract/safety_invariants.mjs   # SC-01..SC-10 safety config invariants
```

The project uses a strict **Documentation → Decision → Contract → Test → Implementation** sequence.

For every requested change:
- name affected FR/NFR/AC IDs;
- update docs first;
- create/update ADRs for architectural decisions;
- update event/API/command contracts before producers or consumers;
- produce a short implementation plan;
- implement the minimum change that conforms to the approved docs;
- add tests mapped to AC IDs;
- never make undocumented assumptions.

If the docs are ambiguous, do not guess. Write the ambiguity into the relevant document as `OPEN DECISION`, propose options, and stop implementation until resolved.
