# Handoff — 2026-10-07: implementation moves to Codex

> **Product owner's decision, 2026-10-07:** everything done so far is handed to Codex, and Codex carries
> out the remaining work. Until now Claude Code implemented and Codex challenged and verified
> read-only (`DUAL_AGENT_PROTOCOL.md` §1). This file is the briefing: read it after `AGENTS.md`, before
> touching anything.

---

## 0. Read first — two things that are not decided

1. **Who is the independent challenger and verifier now — `OPEN`, the product owner's to decide.**
   `DUAL_AGENT_PROTOCOL.md` keeps its rule that *an agent does not verify its own work*: the
   implementer and the reviewer are different agents. With Codex implementing, that role is
   unassigned. **Ask the product owner before the first change on the protocol's mandatory list
   (§2) needs a challenge, and before any formal verification.** Do not challenge or verify your own
   work and record it as independent.
2. **`OD-030` is awaiting the product owner's choice** (§3 below). Do not implement either option.

---

## 1. Where the project stands

| Phase | Status | Where | Proof |
|---|---|---|---|
| 0 Documentation | COMPLETE, approved | `docs/`, `contracts/` | — |
| 1 Simulator | COMPLETE 2026-09-15 | `src/dotnet/EquipmentSimulator/` | AC-018, AC-019, AC-020, PROP-03 |
| 2 Edge gateway, OPC UA, Modbus | COMPLETE 2026-09-22 | `src/dotnet/EdgeGateway/`, `EquipmentSimulator.Protocols/` | AC-001, 002, 021, 022, 023; LOAD-001 |
| 3 Kafka event backbone | COMPLETE 2026-09-30 | `src/dotnet/EventBackbone/` | AC-026, 027, 045 |
| 4 Operational data, Operations API | COMPLETE 2026-10-02 | `src/dotnet/OperationsService/` | AC-028, 029, 030, 046, 047 |
| **5 AI training and inference** | **Requested 2026-10-07 — Definition-of-Ready NOT READY** | none yet (first Python) | AC-008, 024, 025, 032 |
| 6–12 | not requested | — | — |

Every phase's decisions, challenges and verifications are in `reviews/phase-N/` (raw transcripts
verbatim, plus classifications). The status of record is `docs/10-human-review/v0.3/HUMAN_APPROVAL.md`;
`CLAUDE.md` and `AGENTS.md` mirror it. `main` is pushed and CI is green as of `6298383`; the Phase 5
documentation commits after it are local until pushed.

---

## 2. Phase 5 — what remains before any code

The Definition-of-Ready challenge (`reviews/phase-5/CODEX_DOR_CHALLENGE_raw.md`) found ten items.

| Item | State |
|---|---|
| P5-DOR-003 Python consumer vs OD-009 | **resolved** — `OD-024` A |
| P5-DOR-004, 005 labels and dataset | **resolved** — `OD-025` A; `contracts/jsonschema/v1/dataset-manifest.schema.json` written |
| P5-DOR-006 model before the publisher | **resolved** — `OD-026` A (configured `SHADOW` model only) |
| P5-DOR-007 prediction identity | **resolved** — `OD-027` A (UUIDv5, `EVENT_CONTRACTS.md` §2.1) |
| P5-DOR-008, 009 features, aggregation, byte form | **resolved** — `OD-028` A, but its duplicate rule **depends on `OD-030`** |
| P5-DOR-010 model outputs | **resolved** — `OD-029` A′ (the challenger's: `confidence` independent of `oodScore`) |
| Telemetry identity collides across an equipment restart (P5-ODC-001/003) | **`OD-030` — awaiting the product owner** |
| P5-DOR-002 no ADR for any Python dependency; licence gate and CI are .NET only | **open** — next, after OD-030 |
| P5-DOR-001 no test specifications for AC-008, 024, 025, 032 | **open** — last |

**Order of work, once OD-030 is decided:**

1. Apply OD-030 — documents and contracts first (a `v2` telemetry topic touches about 24 files: the
   register, bootstrap, gateway producer, operations projector, tests, documents).
2. **ADR-0025, the Python stack** (P5-DOR-002): environment and lock tooling, Kafka client, JSON Schema
   validator, numeric and gradient-boosting libraries (OD-029 names gradient boosting), MLflow server
   runtime for tests, test runner. Each choice needs alternatives and evidence, as ADR-0021, ADR-0022
   and ADR-0024 did, and a challenge (see §0.1). Then extend `scripts/license-scan.mjs` to the Python
   lock file, and add a Python job to `.github/workflows/ci.yml` (ADR-0023).
3. **Test specifications** for AC-008, AC-024, AC-025, AC-032 in `TEST_SPECIFICATIONS.md` (a new
   §4d), plus the OD-024 conformance suite and the OD-025 dataset reproducibility test. Remove the
   Phase 5 row from §7a. `tests/contract/ac_traceability.mjs` must still pass.
4. Re-run the Definition-of-Ready check; record **READY**; ask the product owner to start.
5. Implement in steps, entry conditions first (ADR group B), as Phases 3 and 4 did — see
   `IMPLEMENTATION_PLAN.md` for how each step was recorded.

### OD-030 in one paragraph

`factory.telemetry.v1`'s duplicate identity is `(equipmentId, sequence)`, and `sequence` resets to 0
on an equipment restart, so the identity collides; the canonical record has no field naming the
boot. The challenger agreed with **A** — the gateway names the boot: `gatewayEpoch` plus a per-equipment
ordinal it increments on each restart it detects (name it neutrally, e.g. `sourceEpochOrdinal`, because
the 49.7-day `sourceEpochMs` wrap counts too) — but ruled that changing a duplicate identity is a
**semantic change, so `v2`**. The choice put to the product owner: **A-v2** (new major topic, fields
required — recommended, and cheapest now while nothing outside the repository consumes telemetry) or
**A-v1** (additive optional fields, the v1 identity scoped "within one boot"). Conditions either way are
in `reviews/phase-5/CODEX_OD-030_CHALLENGE_raw.md`.

---

## 3. How this repository works — rules that are not in the code

**Gate and approval.** Never infer approval. A phase starts only when the product owner asks for it,
and only after its Definition-of-Ready check is READY. Record a phase COMPLETE only after asking.
Phase 6 and later are not requested.

**Sequence.** Documentation → decision → contract → test → implementation. Ambiguity becomes an
`OD-nnn` in `docs/08-roadmap/OPEN_DECISIONS.md` with options, costs and a recommendation — never a
guess. An OD is resolved only by the product owner; record the decision block and apply every
condition the challenge attached, in the documents, before code.

**Challenges and verification** (subject to §0.1). The mandatory list is `DUAL_AGENT_PROTOCOL.md` §2.
Save every challenge and verification **verbatim** to `reviews/phase-N/`, and classify every finding
(`ACCEPTED`, `PARTIALLY_ACCEPTED`, `REJECTED_WITH_EVIDENCE`, `HUMAN_DECISION_REQUIRED`). A phase is
done at **P0 = 0 and P1 = 0** (`DEFINITION_OF_DONE.md`).

**Commits.**
- Messages in **Korean**, in the product owner's own voice: what changed and why, plainly. The
  history is read as the product owner's work.
- **No AI attribution of any kind** — no `Co-Authored-By`, no "Generated with" line.
- Git identity is the repository's local config (`ParkShinUng`); never change the global config.
- **Push only when the product owner asks.** After a push, check CI; do not re-run a failed CI to make
  it pass before the cause is known.
- Never commit secrets; `.claude/`, `.env*` and `.local/` (local ES256 keys) are ignored.

**Honesty in records.** A mitigation is not a fix; a cause not demonstrated is "unproven". When a
test fails, capture the full message before changing anything. When a test passes first time, break
the thing it guards on purpose and see it fail (every Phase 4 suite was checked that way).

---

## 4. Commands

```bash
# contract and document checks - all four must pass before every commit
node tests/contract/validate_examples.mjs
node tests/contract/consistency_check.mjs
node tests/contract/safety_invariants.mjs
node tests/contract/ac_traceability.mjs

# .NET (SDK 10.0.4xx per global.json; Node 24.13.0 per .nvmrc)
dotnet restore src/dotnet/Mair.sln --locked-mode
node scripts/license-scan.mjs                       # licence gate over the restored graph
dotnet build src/dotnet/Mair.sln --no-restore
dotnet test src/dotnet/Mair.sln --no-build -m:1     # -m:1: test projects one at a time (ADR-0023)
```

Docker must be running: the Kafka (`apache/kafka:4.3.1`) and PostgreSQL (`postgres:18.6`) suites
start pinned containers through Testcontainers and **fail, never skip**, without it. Tokens for the
Operations API locally: `node scripts/mint-token.mjs --sub <name> --roles viewer[,operator]`.

**On this machine**: the full suite takes about 7 minutes and the host has run out of memory during
long runs — run `OperationsService.Tests` alone when only it changed, and always keep the whole test
log (`> file 2>&1`), so a failure's message is never lost to a filter.

---

## 5. Traps already found — do not rediscover them

| Trap | What to do |
|---|---|
| `NpgsqlDataSource.ConnectionString` omits the password | build role data sources from the original string (`ProjectionRig.Role`) |
| PostgreSQL grants `EXECUTE` on a new function to `PUBLIC` | `REVOKE ... FROM PUBLIC` **after** `CREATE FUNCTION` |
| `Corvus.Text.Json` ships analyzer CTJ001, an error under warnings-as-errors | use `"name"u8` literals with `System.Text.Json` |
| `ac_traceability.mjs` counts any `AC-nnn` mentioned in `TEST_SPECIFICATIONS.md` outside §7a | refer to a pending phase's criterion in words |
| `ContractSchemaTests.EverySchemaLoads` pins the schema count (9) | update it deliberately when a schema is added |
| xUnit runs test classes in parallel; an `ActivityListener` is process-wide | scope diagnostic counts to a trace ID (TRACE-001) |
| Six consumers in one classic-protocol group polled from one thread took minutes to settle | each consumer runs its own loop (`OperationsProjector.RunAsync`) |
| After a PostgreSQL restart, pooled connections are dead | data sources clear their pool on an unavailable result; tests clear theirs (`ApiRig.RestartPostgresAsync`) |
| Tests that are not about a timeout used the production 250 ms Modbus timeout | `ModbusOverTheWireTests.Patient` (5 s) |
| Shell heredocs with quotes broke repeatedly on this Windows host | write scripts to files and run them |

---

## 6. Open and unproven

- **OPC UA real-path intermittent** (`TEST_SPECIFICATIONS.md`, known intermittent): mitigated by `-m:1`;
  the starved-CPU explanation is consistent with every captured message and not proven.
- **One staleness-header failure** in API-001 (5 instead of 0), seen once and not reproduced; the test
  now waits for a fresh caught-up check before asserting.
- `factory.faults.v1` has no contract until Phase 11 (`OD-020`); the demo profile is refused at startup
  until Phase 11 (AC-031).

---

## 7. Map

| Need | Look in |
|---|---|
| Requirements, criteria | `docs/01-requirements/` |
| Architecture, failure model, time and quality | `docs/02-architecture/` |
| Event, Kafka, API contracts | `docs/03-contracts/`, `contracts/` |
| Per-service design | `docs/11-service-design/` |
| Decisions | `docs/07-adr/` (ADR-0001–0024), `docs/09-decisions/` (DEC), `docs/08-roadmap/OPEN_DECISIONS.md` (OD-001–030) |
| Plan and per-step records | `docs/08-roadmap/IMPLEMENTATION_PLAN.md` |
| Tests and their automation notes | `docs/06-development/TEST_SPECIFICATIONS.md` |
| Versions and licences | `docs/06-development/TOOLCHAIN.md`, `scripts/license-evidence.json` |
| Every challenge and verification | `reviews/` |
