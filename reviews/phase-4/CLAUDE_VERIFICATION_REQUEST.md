# Phase 4 — Verification request to Codex

> Formal verification, read-only. `DUAL_AGENT_PROTOCOL.md` §1: Codex may recommend a fix; Claude
> implements it. Mandatory under §2 — this phase touches **persistence**, **data ownership**,
> **Kafka semantics**, **retries**, **timeouts**, **idempotency**, **security boundaries**,
> **failure recovery**, **schema versioning** and **major dependency**.
>
> `DEFINITION_OF_DONE.md` requires **P0 = 0 and P1 = 0**, and **every finding classified**.

## What was built

Commits after `193596e` (Phase 3 complete): `git log --oneline 193596e..HEAD`.

| Component | Files |
|---|---|
| Migrations, runner, canonical dump | `OperationsService/Migrations/operations/0001`–`0003`, `Database/MigrationRunner.cs`, `CanonicalDump.cs` |
| Roles, retention, store-failure classification | `Database/OperationsRoles.cs`, `RetentionJob.cs`, `StoreFailures.cs` |
| Projector and rebuild | `Projection/Projections.cs`, `OperationsProjector.cs`, `ProjectionRebuild.cs` |
| API | `Api/OperationsApi.cs`, `ReadModel.cs`, `Problems.cs`, `CircuitBreaker.cs`, `ProjectionStatus.cs`; `Auth/OperationsAuth.cs` |
| Host | `OperationsHost.cs`, `Program.cs` |
| Shared consumer changes | `EventBackbone/ContractConsumer.cs` (tombstones, caught-up, outage path), `TopicRegister.cs` |
| Tooling | `scripts/mint-token.mjs`, `scripts/license-scan.mjs` (PostgreSQL), `.github/workflows/ci.yml` (`-m:1`) |
| Tests | `OperationsService.Tests/*` (80), `EventBackbone.Tests/TombstoneTests.cs`, `DependencyOutageTests.cs` |
| Contract changes | `contracts/openapi/operations-api-v1.yaml`, `control-outcome.schema.json` (`fromMode`), `prediction.schema.json` (correlation), examples |

Locally every project passes when run (`-m:1`); the last run of all four together was stopped by the
host for low memory, so each was run alone. Not yet pushed — CI has not seen Phase 4.

## Decisions this phase took

`OD-011`–`OD-023`, `ADR-0024` (+ group B status), the `ADR-0023` amendment (`-m:1`), and the
read-model mapping (`OPERATIONS_API.md` §10a). Every one was challenged by you before it was decided;
the transcripts are in `reviews/phase-4/`.

## What to challenge

Cover this list, and do not stop at it.

1. **Determinism (AC-028, AC-046).** Is every projected value a function of the record and its
   offset? Any `now()`, any order dependence, any type the canonical dump formats ambiguously? Does
   first-wins on the audit tables hold under every redelivery and replay?
2. **The outage path** in `ContractConsumer` (OD-022): pause, seek, retry in hand, polling, revocation.
   Can a record after N be handled before N? Can N be committed without being handled? Can the loop
   evict the consumer, spin, or leave partitions paused for good? Is anything misclassified as an
   outage that should be dead-lettered, or the reverse (`StoreFailures`)?
3. **Rebuild** (`ProjectionRebuild`): the tables it deletes from, the rewind order, a crash between
   rewind and delete, and what a replay into the insert-only tables really restores.
4. **Privileges and retention** (OD-023): can any runtime role change an audit row by any route —
   functions, views, `TRUNCATE`, partitions created by `ensure_partition`? Is the retention function
   safe against a crafted table name or bound?
5. **The API against the contract**: every route and status, the harness itself (could it pass a
   nonconforming response?), keyset paging, the trace's single statement, the readiness gate, the
   staleness header, the breaker, problem bodies for 401/403/429/503.
6. **Token validation** (ADR-0024): anything the policy table names that the code does not enforce.
7. **Do the tests prove the ACs, or only appear to?** AC-028, AC-029, AC-030, AC-046, AC-047 — and
   the ones that were changed after a failure (staleness wait, Modbus patient timeout, harness lock).
   Any bound loose enough to pass a wrong implementation?
8. **What remains open**: the OPC UA intermittent, mitigated by `-m:1` and unproven; the staleness
   failure seen once and not reproduced.
9. Anything else.

## Output format

`P4-COD-nnn · severity (P0/P1/P2/P3) · file:line · defect · why it matters · fix`, then a verdict:
`ACCEPT` (P0 = 0 and P1 = 0) or `REJECT`. Do not modify any file.
