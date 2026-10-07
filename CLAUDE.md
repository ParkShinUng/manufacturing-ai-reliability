# Claude Code project instructions

This file intentionally mirrors the platform-neutral rules in `AGENTS.md`.

Read `MASTER_SPEC.md` and the full reading order in `README.md` before implementation.

## Handoff — read `HANDOFF.md` next

**From 2026-10-07 Codex carries out the remaining work** (the product owner's decision). `HANDOFF.md`
is the briefing: the state of every phase, the ordered next steps for Phase 5, the pending
`OD-030`, the working rules that are not written in code, the commands, and the traps already found.
**Who now challenges and verifies independently is `OPEN`** — ask the product owner before the first
mandatory-list challenge; an agent does not verify its own work (`DUAL_AGENT_PROTOCOL.md` §1).

**Commit messages: Korean, in the product owner's voice, with no AI attribution line of any kind.**
Push only when asked.

## Implementation gate — CHECK THIS FIRST, EVERY SESSION

**Specification status: v0.3. Human decision: APPROVED_WITH_CONDITIONS — all conditions satisfied
(2026-09-15). Gate: OPEN. Phases 1 and 2: COMPLETE (2026-09-22). Phase 3: COMPLETE (2026-09-30). Phase 4: COMPLETE (2026-10-02). Phase 5: requested 2026-10-07, NOT READY (DoR).**

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
dependency is added.

The product owner asked for Phase 2 on **2026-09-16** and it completed on **2026-09-22**: the Edge
Gateway in `src/dotnet/EdgeGateway/`, protocol servers in `src/dotnet/EquipmentSimulator.Protocols/`,
proving **AC-001, AC-002, AC-021, AC-022, AC-023**, Codex-verified and `LOAD-001` passed.

The product owner asked for **Phase 3** on **2026-09-28**, after its Definition-of-Ready check
resolved `OD-007`, `OD-008` and `OD-009` and `ADR-0021` fixed the Kafka client and broker runtime.
It completed on **2026-09-30**: the Kafka event backbone in `src/dotnet/EventBackbone/` and the
gateway's Kafka egress, proving **AC-026, AC-027, AC-045**, Codex-verified (round 1 REJECT, round 2
ACCEPT) and green in CI.

The product owner asked for **Phase 4** on **2026-09-30**. Its Definition-of-Ready check was
NOT READY, and is **READY** since 2026-10-01 (`reviews/phase-4/`): `OD-011`–`OD-019` resolved,
`ADR-0024` accepted, test specifications written. Implementation **started 2026-10-01** at the product
owner's request, and it completed on **2026-10-02**: the operational store and the Operations API in
`src/dotnet/OperationsService/`, proving **AC-028, AC-029, AC-030, AC-046, AC-047**, Codex-verified
(round 1 REJECT, round 2 ACCEPT) and green in CI.

The product owner asked for **Phase 5** on **2026-10-07**. Its Definition-of-Ready check is
**NOT READY** (`reviews/phase-5/`): **no Phase 5 code** until its blocking items are resolved.
Nothing authorises **Phase 6** — ask first.

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
