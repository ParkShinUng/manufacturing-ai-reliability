# Codex Definition-of-Ready re-check - Phase 4

> Raw output, transcribed verbatim from the `codex exec -s read-only` run of 2026-10-01. Verdict:
> **READY** (P4-DOR-001..010 closed; P4-DOR-011..013 stale wording, fixed the same day).

Re-ran Phase 4 DoR read-only, including `git diff`. Current uncommitted changes are only:

`contracts/openapi/operations-api-v1.yaml`, `docs/06-development/TEST_SPECIFICATIONS.md`, `docs/08-roadmap/IMPLEMENTATION_PLAN.md`.

Verification run:
`validate_examples.mjs` passed: 9 examples, 0 failing.
`consistency_check.mjs` passed: 112 files, 0 violations.
`safety_invariants.mjs` passed: 10 invariants, 0 failing.
`ac_traceability.mjs` passed: 47 ACs, 27 mapped, 20 pending in unstarted phases.

**Prior P4-DOR Items**
- `P4-DOR-001` CLOSED — AC-006 moved to Phase 7; Phase 4 now proves AC-047 store/query only. Evidence: `ACCEPTANCE_CRITERIA.md:91`, `:95`; `IMPLEMENTATION_PLAN.md:163`.
- `P4-DOR-002` CLOSED — AC-006 remains live-producer proof; fixtures now map to AC-047 only. Evidence: `ACCEPTANCE_CRITERIA.md:95-99`; `TEST_SPECIFICATIONS.md:357-361`.
- `P4-DOR-003` CLOSED — canonical dump and range rebuild are defined. Evidence: `OPERATIONAL_DATA.md:64-75`; `:59-62`.
- `P4-DOR-004` CLOSED — vague aggregate replaced by latest per-second reading rule. Evidence: `OPEN_DECISIONS.md:913-925`; `TEST_SPECIFICATIONS.md:370-376`.
- `P4-DOR-005` CLOSED — Phase 4 creates `operations` schema only; `control` belongs to Phase 7 Control Service. Evidence: `OPERATIONAL_DATA.md:27-30`, `:42-43`.
- `P4-DOR-006` CLOSED — auth authority/mechanism pinned: ES256 JWT, issuer/audience/roles/sub/key handling, local token script, ADR-backed. Evidence: `OPERATIONS_API.md:73-79`; `ADR-0024...md:95-112`.
- `P4-DOR-007` CLOSED — readiness, staleness, rate limit, timeout, retention numbers are specified. Evidence: `OPERATIONS_API.md:36-40`, `:55-65`; `OPEN_DECISIONS.md:1007-1012`.
- `P4-DOR-008` CLOSED — `CorrelationTrace.decision` and `command` are nullable with “no record observed” semantics. Evidence: `operations-api-v1.yaml:460-488`; `OPEN_DECISIONS.md:782-790`.
- `P4-DOR-009` CLOSED — ADR-0024 is Accepted and pins Npgsql, migration runner, ASP.NET Core, JwtBearer, YamlDotNet, PostgreSQL/Testcontainers. Evidence: `ADR-0024...md:1-7`, `:157-163`.
- `P4-DOR-010` CLOSED — Phase 4 tests now exist and AC traceability passes. Evidence: `TEST_SPECIFICATIONS.md:365`, `:380`, `:396`, `:414`; `PROJ-001` at `:165`.

**New Findings**
- `P4-DOR-011` — P2 — `contracts/openapi/operations-api-v1.yaml:156` — stale/overbroad trace route summary says full `telemetry -> prediction -> decision -> command`, while OD-013/AC-047 changed Phase 4 trace to prediction-started chain with `window` per-second readings — this can mislead implementers and contract readers — fix summary/description to name AC-047 store-and-query and note AC-006 live-chain proof is Phase 7.
- `P4-DOR-012` — P3 — `docs/11-service-design/OPERATIONS_API.md:93` — test strategy still says `AC-006 trace` — AC-006 is Phase 7 and Phase 4 test is TRACE-001/AC-047 — fix to `AC-047 trace store-and-query; AC-006 in Phase 7`.
- `P4-DOR-013` — P3 — `docs/06-development/TOOLCHAIN.md:63-65` — text still says Phase 4 “owes its test specifications (P4-DOR-010)” even though §4c now exists and traceability passes — stale readiness note — fix to say test specs are written and ADR-0024 Group B is verified by the Phase 4 spike.

**Test Spec Falsifiability**
`PROJ-001` is falsifiable and proves AC-046: canonical dumps equal, replay range constrained, idempotent second replay, no writes outside projection tables.

`PROJ-002` is falsifiable and proves AC-028: real broker/PostgreSQL, duplicate/late/equal-time fixtures, second empty DB, byte-identical canonical dumps, catalog checks for nondeterminism.

`AUDIT-001` is falsifiable and proves AC-029: exact equipment/time queries, keyset paging under inserts, retention boundary behavior, mode-transition fields, invalid transition DLQ.

`API-001` is falsifiable and proves AC-030: every documented route/status exercised, response status/media/headers/body/formats validated, coverage check required.

`TRACE-001` is falsifiable and proves AC-047: linked schema-valid records, missing-link/null case, unknown/404 case, one SQL statement via diagnostics, P95 ≤ 200 ms.

READY. Blocking IDs: none.
