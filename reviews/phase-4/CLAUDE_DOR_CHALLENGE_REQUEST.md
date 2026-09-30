# Phase 4 Definition-of-Ready — challenge request to Codex

> Read-only challenge, before any Phase 4 work and before any option is drafted. `DUAL_AGENT_PROTOCOL.md`
> §1: Codex challenges, Claude implements, the human decides. Same order as Phase 3's DoR check:
> questions first, options only after.
>
> The product owner asked for Phase 4 on **2026-09-30**. The request authorises the phase; it does
> not make it ready. Implementation waits for this check (`DEFINITION_OF_READY.md`).

## What Phase 4 is

`IMPLEMENTATION_PLAN.md`: PostgreSQL projections and read models · rebuild-from-Kafka · Operations
API per the OpenAPI contract · correlation-chain retrieval. Proof: **AC-006, AC-028, AC-029, AC-030,
AC-046**. Service designs: `docs/11-service-design/OPERATIONAL_DATA.md`, `OPERATIONS_API.md`;
contract `contracts/openapi/operations-api-v1.yaml`.

Phase 3 is complete: 14 topics bootstrapped, the shared consume-validate-DLQ component
(`src/dotnet/EventBackbone/ContractConsumer.cs`), the gateway producing `factory.telemetry.v1` and
`factory.equipment-states.v1`, replay mechanics with `cg.operations-projector.v1` replay-eligible.

## What the readiness check already found, and is not asking about

Ordinary work items, listed so you do not re-find them — challenge only the classification:

- **No ADR selects the dependencies.** `TOOLCHAIN.md` pins PostgreSQL major 18, nothing else: no
  .NET PostgreSQL driver, no migration tool, no ASP.NET Core hosting decision recorded as an ADR,
  no OpenAPI response validator for AC-030, no test runtime for PostgreSQL. Each needs an ADR and a
  challenge (§2: persistence, major dependency).
- **AC-006, AC-028, AC-029, AC-030 have no test specification.** They sit in `TEST_SPECIFICATIONS.md`
  §7a, whose rule fails the build once the Phase 4 heading reads IN PROGRESS. Only AC-046 has one
  (`PROJ-001`).

## What to challenge

1. **Most of what Phase 4 projects has no producer yet.** Only telemetry and equipment state are
   produced (Phase 2/3). Predictions are Phase 5, safety decisions Phase 6, control outcomes and
   commands Phase 7. **AC-006** asks for the telemetry → prediction → decision → command chain in one
   query; **AC-029** for audit records of decisions, outcomes and mode transitions. Is this the same
   defect as Phase 3's AC-003 (OD-007) — a criterion assigned to a phase that cannot observe what it
   claims? Would proving it from contract-example fixtures be honest, or does it only appear to
   prove it? What is the smallest honest Phase 4 obligation for each?

2. **"Byte-identical" rebuild (AC-028, AC-046) versus what a projection naturally stores.** An
   ingestion timestamp, a surrogate key from a sequence, a `now()` default, row order, float
   formatting, or aggregate windows closed by wall clock would each make a rebuild differ while
   being correct. Does anything in `OPERATIONAL_DATA.md` §7–§10 or the schemas already rule these
   out? Is "byte-identical" defined anywhere — on which representation (rows, a canonical dump,
   API responses)?

3. **`telemetry_aggregate_1s` has no contract.** Which statistics, which window alignment (event
   time `occurredAtUtc` or arrival), what happens to late and out-of-order records, how quality
   flags and null measurements aggregate. Without this, a projection is an invention. Is it a
   contract gap, and does it interact with (2)?

4. **Ownership and migration of Control Service tables.** `OPERATIONAL_DATA.md` §7 lists
   `control_state` and `command_idempotency` as control-service–owned and authoritative, §9 says
   migrations are applied "by a single owner service", `OPERATIONS_API.md` hosts the projectors.
   Who owns the schema for tables whose writer (Phase 7) does not exist yet, and does Phase 4
   create them?

5. **Security for the first HTTP hop.** The OpenAPI contract says bearer JWT with `viewer` and
   `operator` role claims. Nothing names the issuer, key distribution, or how a local/demo run gets
   tokens. `DEFINITION_OF_READY.md` §8 requires the mechanism be *named*. Is it?

6. **Numbers that are not numbers.** "serve once projection lag < threshold" (`OPERATIONS_API.md`
   §9), the staleness header and field semantics, retention job behaviour against partitioned
   tables (§11/§15), rate limiting (§13). Which are unspecified in a way an implementer would have
   to invent?

7. **Anything else in the Phase 4 contracts that cannot be implemented as written** — the OpenAPI
   file itself (every route, error shape, pagination/keyset cursor semantics, the demo endpoint's
   absence outside the demo profile, which is AC-031 and Phase 11), duplicate identity for each
   projected topic, idempotent upsert keys, and restart recovery.

## Output format

`ID (P4-DOR-001 onward) · severity (P0/P1/P2/P3) · file:line · what is wrong or unanswerable · why
it matters · what must be decided or fixed before Phase 4 starts`, then a readiness line: **READY**
or **NOT READY** with the blocking IDs.

Do not propose an implementation. Where a decision is needed, say what has to be decided and what
each answer costs — the product owner decides, not the agents.
