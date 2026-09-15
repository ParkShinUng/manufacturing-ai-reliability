# Manufacturing AI Reliability Platform — Specification Repository

This repository is intentionally **documentation-first**. The documentation is the product contract; code is a derived artifact.

> **Status: v0.3 Implementation Ready Candidate — awaiting human approval.**
> Start at `docs/10-human-review/v0.3/HUMAN_REVIEW_SUMMARY.md` if you are the reviewer.
> Implementation has **not** started and must not start until
> `docs/10-human-review/v0.3/HUMAN_APPROVAL.md` records approval.

## Required reading order for humans and AI agents
1. `MASTER_SPEC.md`
2. `docs/00-product/PROJECT_CHARTER.md`
3. `docs/01-requirements/FUNCTIONAL_REQUIREMENTS.md`
4. `docs/01-requirements/NON_FUNCTIONAL_REQUIREMENTS.md`
5. `docs/01-requirements/ACCEPTANCE_CRITERIA.md`
6. `docs/02-architecture/SYSTEM_ARCHITECTURE.md`
7. `docs/02-architecture/FAILURE_MODEL.md`
8. `docs/03-contracts/EVENT_CONTRACTS.md`
9. `docs/03-contracts/API_AND_COMMAND_CONTRACTS.md`
10. `docs/04-ai/AI_SAFETY_AND_MLOPS.md`
11. `docs/05-operations/OBSERVABILITY_AND_SLO.md`
12. `docs/06-development/DEVELOPMENT_PROTOCOL.md`
13. `docs/02-architecture/EQUIPMENT_MODEL_AND_STATE.md` — simulator physics + equipment state machine
14. `docs/02-architecture/CONTROL_MODE_STATE_MACHINE.md`
15. `docs/02-architecture/TIME_AND_DATA_QUALITY.md`
16. `docs/03-contracts/OT_PROTOCOL_MAPPING.md` — OPC UA / Modbus address maps
17. `docs/03-contracts/KAFKA_TOPOLOGY_AND_SEMANTICS.md`
18. `docs/03-contracts/SAFETY_CONFIGURATION.md`
19. `docs/11-service-design/` — 12 implementation-ready service designs
20. `docs/09-decisions/` — how each architecture decision was challenged
21. relevant ADRs under `docs/07-adr`
22. `docs/06-development/TEST_SPECIFICATIONS.md`
23. `docs/08-roadmap/IMPLEMENTATION_PLAN.md`

## Verification you can run now

Both are dependency-free and run before any application code exists:

```bash
node tests/contract/validate_examples.mjs    # documented examples vs JSON Schemas
node tests/contract/consistency_check.mjs    # cross-document naming/contradiction checks
```

## Contract authority

Machine-readable contracts in `contracts/` are **authoritative** over contract prose in
`docs/03-contracts/` (ADR-0017). Every example in the prose is a validated instance, enforced by the
first script above.

## Core rule
**No undocumented implementation decision.** If implementation requires behavior not defined by the docs, stop implementation and create/update the relevant specification or ADR first.
