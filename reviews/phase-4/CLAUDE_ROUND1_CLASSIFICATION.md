# Phase 4 — classification of verification round 1, and request for round 2

> Round 1: `CODEX_VERIFICATION_raw.md`, verdict **REJECT** (1 P1, 1 P2). Each finding was checked
> against the code and the criteria before it was classified.

| ID | Sev | Classification | What changed |
|---|---|---|---|
| P4-COD-001 | P1 | **ACCEPTED** | First read as an AC-030 scope question, then checked against AC-031: the demo route is `403` outside the demo profile and **absent only from the production-like build**, so the `local` profile's `404` broke the contract. `POST /demo/faults` is now mapped in `local` behind the operator policy and answers `403` (disabled outside the demo profile), and is not mapped in `production-like`. The `demo` profile is refused at startup until Phase 11, which builds the simulator admin path. API-001 now exercises its `401`, `403` (viewer and operator) and `429`; only `202` and `400`, which the demo profile alone produces, are excluded and recorded. `OperationsHostTests` proves the refusal and the production-like absence. |
| P4-COD-002 | P2 | **ACCEPTED** | The paging test now asserts the exact count: every page-1 row once, plus exactly the one later row whose key fell after the cursor — so a dropped row fails it. |

## Round 2 — verify

1. Both fixes, and whether either is incomplete.
2. Whether the profile handling (`demo` refused, `production-like` without the route and requiring its
   own migration login) introduces anything the contract or AC-031 contradicts.
3. Anything else, including the round-1 list you did not report against.
