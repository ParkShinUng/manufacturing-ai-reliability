# Documentation Changelog

## Phase 1 - Equipment Simulator domain core (2026-09-15)

First application code in the repository. The gate was opened by the human approval recorded in
`docs/10-human-review/v0.3/HUMAN_APPROVAL.md`, and Phase 1 was then explicitly requested.

### Documentation corrected before implementing (Definition of Ready)
- **Phase 1/2 boundary.** `IMPLEMENTATION_PLAN.md` listed the protocol server endpoints under
  Phase 2 while its ordering rationale implied Phase 1. The explicit deliverable list wins: the
  **protocol servers are Phase 2**, and Phase 1 is the library-free domain core. This also means no
  OPC UA or Modbus dependency has been chosen yet - that needs an ADR and a Codex challenge.
- **AC-001 moved to Phase 2.** It requires canonical telemetry and a gateway process, neither of
  which exists in Phase 1, so it could never have been proven there.
- **.NET SDK 10.0.401 -> 10.0.400.** The pinned patch was asserted, never verified, and is not
  installed; `TOOLCHAIN.md`'s own procedure says to correct the document first.

### Model findings recorded rather than tuned away
- **Additive noise needed a physical floor.** At rest `currentA = 0 +- N(0, 0.06)` goes negative,
  which is `VALUE_OUT_OF_RANGE` on a **safety-required** channel and trips an idle machine within
  2 s. Readings are now clamped at a channel's lower bound where that bound is physical; the upper
  bound is deliberately left unclamped so a genuine excursion stays visible.
- **The degradation rate-deviation check is measured against the slew-limited expected rate**, not
  the raw command. A full-range start takes 6.7 s at 15 %/s, so the literal reading put every cold
  start into `DEGRADED` for 30 s, contradicting `T4`.
- **The over-temperature trip has 0.46 degC of margin.** The only physics path to 120 degC is
  `COOLING_DEGRADATION` at full rate, asymptotic at 120.46 degC. Reachable but slow and sensitive to
  `T_ambient`. Recorded in `EQUIPMENT_SIMULATOR.md` 11; changing it is a model decision.

### Implemented
`src/dotnet/EquipmentSimulator/` - physics and degradation, the T1-T12 state machine with its three
forbidden transitions, all 10 fault profiles, L3 protective trips, the 30 s dead-man revert, and
`sequence` / `sourceEpochMs` assignment. The assembly references **nothing outside the framework**,
which is how AC-020's "with the entire platform stopped" is asserted rather than claimed.

`DeterministicRandom` is xoshiro256** implemented in-repository: `System.Random`'s algorithm is an
implementation detail that has changed between .NET versions, so a seeded run with it is
reproducible on one runtime only - which does not satisfy NFR-010.

44 unit tests in `src/dotnet/EquipmentSimulator.Tests/` prove AC-018, AC-019, AC-020 and PROP-03.

## 0.3.0 — Implementation Ready Candidate (dual-agent reviewed, awaiting human approval)

Engineering review: **Claude Code (primary engineer) + Codex (independent challenger)**, 3 rounds.
62 reconciled findings — 22 BLOCKER, 27 HIGH, 10 MEDIUM, 3 LOW. 58 resolved, 4 deliberately deferred.

### Safety and control authority
- **ADR-0011**: three-layer authority model. Control Service gains a 12 s fallback watchdog and a
  durable `controlEpoch` fencing token; simulated equipment gains a 30 s dead-man revert. Closes the
  defect that "control survives AI failure" held for inference death but **not** for Safety
  Supervisor death.
- **ADR-0013**: command ordering by structural non-concurrency (2 s command TTL < 5 s inference
  cadence) plus epoch fencing, replacing both a rejected distributed counter and a rejected
  wall-clock ordering scheme.
- **ADR-0014**: manual/operator command origin **removed** from the baseline — it was a documented
  bypass of the deterministic safety path with no state, authorization, or interlock defined.
- **ADR-0015**: OOD is a **hard reject**; the duplicate normative gate list in `MASTER_SPEC` §7 was
  removed in favour of one canonical 13-gate table.
- **ADR-0016**: command origin is authenticated from verified mTLS identity; the self-asserted
  `source` field is **removed** from the command contract.
- **ADR-0019**: model authorization is published to a compacted Kafka topic with a 30 s liveness
  watermark; the Safety Supervisor no longer calls MLflow on the decision path.

### Contracts
- **ADR-0017**: machine-readable contracts in `contracts/` now outrank contract prose.
- **ADR-0018**: telemetry measurements are nullable with a **closed** quality-flag enum; a dead sensor
  is representable for the first time, and substituting a synthetic value is forbidden.
- Seven JSON Schemas (was one): envelope, telemetry, prediction, safety-decision, control-outcome,
  equipment-state, model-authorization, plus safety-config.
- `control.proto` rewritten: `control_epoch`, `AcquireControlLease`, `Heartbeat`, typed enums,
  `google.protobuf.Timestamp`, `authenticated_source`.
- OpenAPI rewritten from a stub into a full contract with schemas, RFC 9457 problem details, keyset
  pagination, and a security scheme.
- **Fixed**: the v0.2 telemetry example omitted three schema-required measurements and would have
  failed its own contract.

### New specifications
- Simulator physics, sensor ranges, degradation equations, and 10 fault profiles.
- OPC UA node map and Modbus TCP register map (neither existed).
- Equipment state machine (7 states, 12 transitions) and control mode state machine.
- Time and data-quality model: clock sources, ±250 ms skew budget, closed flag vocabulary,
  deterministic null aggregation.
- Kafka topology: 7 topics with partitions, retention, DLQ, offset and replay semantics.
- Failure matrix expanded from 10×4 to 32 rows × 13 columns with CONTROL-CRITICAL classification.
- 12 service designs where v0.2 had one paragraph each.
- Safety configuration contract with 10 startup-enforced cross-field invariants.
- Acceptance criteria expanded from 10 to 44; test specifications with executable assertions.
- 10 Mermaid diagrams.

### Process
- `docs/09-decisions/` — 9 Decision Records showing how each decision was challenged.
- `docs/10-human-review/v0.3/` — human review packet.
- Two dependency-free verification scripts under `tests/contract/`, both passing.

### Post-review consistency pass

A self-audit after the dual-agent review found **13 items left behind** - the earlier consistency
checker had passed at 0 violations because it only looked for defects its author had suspected.

- **`SYSTEM_ARCHITECTURE.md` rewritten** - it still listed 8 components (no `mlops-publisher`), still
  said the Safety Supervisor owned control mode (moved to Control Service in ADR-0011), and used a
  non-canonical topic name. It is read-order position 6, so a Phase 1 implementer would have hit the
  old authority model first.
- **`RUNTIME_BEHAVIOR.md`** - added the 25 pp / 60 s cumulative rate budget and the
  last-commanded baseline rule; the file previously described only the per-decision limit, which is
  the exact defect GAP-034 raised.
- **`REPOSITORY_STRUCTURE.md`, `GLOSSARY.md`** - v0.3 directories and ~25 new terms.
- **New: `DEFINITION_OF_READY.md`** - v0.2 had only a Definition of Done, which is how a
  specification can look complete while still forcing an implementer to invent architecture.
- **New: `DUAL_AGENT_PROTOCOL.md`** - roles, the mandatory-participation list, the 20-step workflow,
  ASK/CHALLENGE/VERIFY during implementation, and four honesty rules each drawn from a rule broken
  during this review.
- **Roadmap expanded 8 to 13 phases** (0-12). Records the uncomfortable dependency plainly: AC-011
  and AC-012, which prove the headline reliability claim, cannot run until Phase 7.
- **`DEVELOPMENT_PROTOCOL`, `DEFINITION_OF_DONE`, `TEST_STRATEGY`, `CODING_STANDARDS`,
  `AI_AGENT_CHANGE_TEMPLATE`, `PROJECT_CHARTER`** updated.
- **Directory placeholders** for `observability/`, `deploy/`, `scripts/`, `reports/`, and the four
  `tests/` suites - README only, no executable artifact, because the gate is closed. `reports/` is
  empty on purpose and says so: until a file lands there, no number here is a measured result.
- **`safety-config.demo.json`** now marks `maxAuthorizationStalenessMs` as pending HD-001, so a
  copied config cannot present an unapproved value as approved.
- **Consistency checker extended** with 5 rule groups covering v0.2 leftovers, stale documents,
  required process docs, roadmap completeness, and unapproved values. Each was negative-tested.
  The first version of the roadmap rule had a bug - `` in a JS template literal is the backspace
  character - and reported all 13 phases missing while all 13 were present.

### Human decision (2026-09-15)

| ID | Decision | Outcome |
|---|---|---|
| HD-001 | Max authorization staleness when Kafka is unreachable | **APPROVED** — 60 s |
| HD-002 | Manual/operator command origin | **APPROVED** — keep removed |
| HD-003 | ADR-0011 control authority change | **APPROVED** — ratified |
| **HD-004** | **Overall v0.3 approval** | **APPROVED_WITH_CONDITIONS** |

The three architecture decisions were approved and their downstream documents reconciled:
`maxAuthorizationStalenessMs` is no longer marked pending, and DEC-001/004/009 plus ADR-0011/0014/0019
carry closed human-review sections.

The overall specification was **approved with conditions**. (HD-004 was briefly recorded as
`REJECTED` earlier the same day and then changed; the interim state is noted in `HUMAN_APPROVAL.md`
rather than erased, since an approval record that rewrites its own history is not an audit trail.)

The conditions were stated the same day: **C-1/C-2/C-3 are the three decisions themselves**, and all
three are satisfied with evidence recorded in `HUMAN_APPROVAL.md`.

### Naming (2026-09-15)

Renamed from `Aegis Factory` / `aegis-factory` after a two-round second opinion from Codex plus a
GitHub/PyPI collision search.

| Layer | Was | Now |
|---|---|---|
| Repo slug | `aegis-factory` | **`manufacturing-ai-reliability`** |
| Product name (prose) | Aegis Factory | **Manufacturing AI Reliability Platform** |
| Code token | `aegis` | **`mair`** |

Code token applies to every identifier context: `mair.control.v1` (proto), `mair_ml_core` (Python),
`spiffe://mair/...` (workload identity), `Mair.sln` (.NET), `mair.local` (schema `$id` host),
`urn:mair:equipment:v1` and `opc.tcp://.../mair/` (OPC UA).

**Why the old name was retired:** `aegis` collides with 7+ GitHub projects, **four of them
AI-adjacent** (AI coding-agent frameworks, an AI agent monitor, an AI project-management framework),
is already taken on PyPI, and carries an unrelated defence-system connotation. For a project whose
whole thesis is AI reliability, that is the worst possible neighbourhood to be lost in.

**Why not `-platform` or `-spec` as a suffix:** Codex argued `-platform` oversells a repo with zero
application code, and Claude initially accepted `-spec` instead. On challenge that was withdrawn:
`-spec` names a *phase*, not a thing - wrong the moment Phase 1 lands (now authorized), and if the
project ever stalled the name would advertise the stall. Both suffixes were dropped.

`mair` was checked, not assumed: free on PyPI and npm, and valid as a Python identifier, proto
package segment, DNS label, C# namespace root, SPIFFE trust domain, and URN namespace-specific
string.

Two anti-regression rules were added to `tests/contract/consistency_check.mjs`
(`retired-name`, `token-drift`), both negative-tested.

**Folder rename:** the directory cannot be renamed while a session holds it as its working directory
(diagnosed: the `claude` process itself, which resets cwd after every tool call). A **directory
junction** was created so the new path works immediately without ending the session:

```
Portfolio\manufacturing-ai-reliability  ->  Portfolioegis-factory   (junction)
```

Both paths resolve to the same files; the verification suites pass from either. Run
`scripts/finalize-rename.ps1` after closing the session to replace the junction with a real rename.

### Status
**Gate OPEN. Phase 1 authorized. Implementation NOT started.**
No application code written. Authorization removes a prohibition; it does not issue a task.

Two things approval does not change: every performance figure remains `TARGET (unmeasured)`, and
AC-011/AC-012 cannot run until Phase 7.

## 0.2.0
- Resolved baseline equipment, load, telemetry, inference cadence, persistence, OOD, Kafka topology, and model rollout decisions.
- Added machine-readable contract strategy, gRPC command-path decision, repository structure, toolchain baseline, reason codes, and coding conventions.
- Documentation gate remains closed to application implementation pending user approval.

## 0.1.0
- Established project scope, architecture principles, and document precedence.
- Defined documentation-first implementation protocol.
- Selected event-driven OT + AI architecture with separated safe control path.
- Defined initial requirements, AI safety gates, failure model, contracts, observability, and roadmap.
