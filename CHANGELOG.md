# Documentation Changelog

## ADR-0020 - OT protocol libraries, and the contract defects choosing them exposed (2026-09-16)

Phase 2's prerequisite. `AGENTS.md` forbids adding a dependency without documented rationale, and
`protocol choice` and `major dependency` are both on the mandatory Codex participation list, so the
libraries could not be chosen in a commit message.

### Chosen

`OPCFoundation.NetStandard.Opc.Ua.Server` / `.Client` / `.Configuration` 1.5.378.176 and
`NModbus` 3.0.83, all MIT, pinned in `TOOLCHAIN.md`. The split packages are named deliberately - the
`OPCFoundation.NetStandard.Opc.Ua` meta package has no framework assets and pulls in GDS and
complex-type assemblies neither service needs.

Two findings decided this and are worth recording:

- **The OPC UA licence had to be checked, not assumed.** This stack is widely documented as dual
  licensed - RCL for OPC Foundation corporate members, GPLv2 for everyone else - which would have
  disqualified it for a public repository. The current `LICENSE.txt` is a single MIT licence with no
  membership condition. Secondary documentation still describes the old model in places.
- **`FluentModbus` is disqualified by one requirement.** `EQUIPMENT_SIMULATOR.md` 11 requires an
  out-of-range setpoint to be rejected with Modbus exception `0x03`, never clamped. FluentModbus
  exposes the server as a register buffer; its `RequestValidator` sees unit id, function code,
  address and quantity but **not the value**, and `RegistersChanged` fires after the write. So a bad
  value can be detected and corrected but not refused - which is the silent clamp the spec forbids.
  `NModbus` passes values to `IPointSource.WritePoints` and carries an arbitrary exception code back
  through `InvalidModbusRequestException`.

Hand-rolling Modbus TCP was considered seriously and rejected. The rationale originally cited AC-021;
Codex pointed out that AC-021 catches scaling and word-order disagreement, not framing, transaction
ids or concurrency, so the reasoning was corrected. The asymmetry with this repository's hand-rolled
PRNG is stated explicitly: the PRNG exists because no dependency could provide bit-identical output
per seed (NFR-010) and it is 40 pure lines; a Modbus server is stateful, concurrent and socket-facing.

### Contract defects the challenge exposed

Both were in `OT_PROTOCOL_MAPPING.md`, not in the ADR. A library cannot be chosen against a contract
that is not implementable.

- **`FC06` cannot write the setpoint.** 2.4 said "function code 6/16", but
  `operationRateSetpointPct` is a two-register int32 and `FC06` writes one register. It could only
  ever write half the value - not a malformed request to be rejected, but a *different rate* the
  equipment would act on. Corrected to `FC16` only, quantity exactly 2, starting at the base, with a
  response table for every other case.
- **Modbus TCP cannot enforce a read-only gateway.** The contract said the gateway's session is
  provisioned without write permission so the property is server-enforced. Modbus TCP has no
  authentication, no session identity and no per-client permission model - there is nothing to
  provision. Raised as OD-003 and resolved by the product owner as **option A: two listeners**,
  read-only `5020` for the gateway, write-capable `5021` for the Control Service.

### The part that took longest

One wrong sentence - "a misrouted gateway still cannot write" - was circular, and it had been written
into **nine** documents. It only holds if the gateway is pointed at `5020`. Removing it took several
rounds because each pass fixed only the instances that had been named rather than sweeping for the
claim. What the listener split actually buys is narrower and is now stated the same way everywhere:
with the correct port, no gateway defect or compromise can produce a write, because the connection
carries no write function codes; a gateway misconfigured to `5021` can write, and only network policy
stops it. The failure mode moves from "any gateway bug can write" to "only a wrong port can write".

`SAFETY_SUPERVISOR.md` had the same wrong model in a different service - "no equipment credentials,
enforced by provisioning" enforces nothing when the protocol has no credentials - and now states what
holds for both protocols: no OT client code and no route into the OT zone.

One Codex finding was **rejected with evidence**: that the word "session" throughout the state
machine implies Modbus is session-based. It describes connectivity, which both protocols have, and
`session_timeout` is a normative constant wired through the transition table, the diagram, the C#
implementation and its tests. Addressed by defining the term at its source instead.

## Phase 1 verification - dual-agent review and the two decisions it produced (2026-09-15)

Three Codex rounds against commit `8d24190`. Codex rejected twice, then accepted.
Transcripts and classifications: `reviews/phase-1/`.

### Defects found and closed

Codex returned 1 P0 and 5 P1 on the first pass and was right on all six; nothing was rejected.

- **P0 - an operator could reset a machine whose safety sensor was still dead.** The reset guard
  evaluated its own, smaller set of protective conditions than the tick path. The root cause was not
  the missing conditions but that **two independent evaluations of "is this machine safe" existed**
  and the one guarding the reset was the weaker; the reset now reads what the tick just computed.
- **The `sourceEpochMs` wrap reset fired on one rollover in twenty-five.** `elapsed % 2^32 == 0`
  cannot hold at a 100 ms cadence: elapsed is always a multiple of 100, 2^32 is not, and they
  coincide only at LCM = 107374182400 ms. Consumers would have read 24 of every 25 wraps as a gap.
- **`STOPPING` could not latch `FAULT`.** Section 3.4 says any protective condition is sufficient;
  the `T9` table omitted `STOPPING`. A machine slewing down through an over-temperature condition
  reached `IDLE` unlatched, escaping operator acknowledgement. The prose was right, the table wrong.
- **The drive-tracking degradation condition was a tautology** - it compared the applied rate against
  a variable assigned the applied rate, so it could never fire.
- **`COMM_LOSS` was inert.** Its AC-018 test proved the fault-injection API existed, not that the
  profile produced a signature. Claude argued once that onset belongs to injection and was wrong:
  AC-018 requires the PROFILE to produce the signature.
- **A round 1 fix created a new defect.** The drive-lag test hook froze the applied rate while a
  protective trip set only the setpoint, so a demo hook could defeat "FAULT: rate forced 0" - the
  one property AC-020 exists to guarantee.

### What the fixes uncovered, and how the product owner resolved it

Two findings could not be closed by an implementer, because both meant a documented safety condition
had no cause in the model. Raised as OD-001 and OD-002 and decided by the product owner:

- **OD-001, resolved by option A.** No fault profile could make a drive stop tracking its setpoint,
  so `T7` had a trigger nothing could pull. Added **`DRIVE_STUCK`** - `r_applied` freezes at
  `T_stuck`, demo default 60 s. **AC-018 now covers 11 profiles, not 10.** The demo-only
  `InjectDriveLag()` hook was removed: a test hook that duplicates a profile is a second way for the
  two to drift apart.
- **OD-002, resolved by option B.** Two of the three sensed protective thresholds were above
  anything the model could produce - 25 mm/s against a 15.4 mm/s worst case, 32 A against 19.2 A -
  and the third had 0.46 degC of margin. **The innermost safety layer was unexercisable by any
  scenario the demo runs.** Fault severity was raised rather than the safety limits lowered:

  | Constant | Was | Now | Worst case | Limit |
  |---|---|---|---|---|
  | vibration health coupling | 6.0 | 12.0 | 28.6 mm/s | 25.0 |
  | `OVERLOAD` load factor | 1.6 | 2.8 | 33.6 A | 32 |
  | `COOLING_DEGRADATION` cap on `c` | 0.9 | 0.95 | 133.3 degC | 120 |

  Lowering the limits would have compressed the `DEGRADED` -> `FAULT` ladder that `T7`/`T8`/`T9`
  depend on. A simulator that cannot produce a dangerous machine is a limitation of the fault model,
  not evidence that 25 mm/s is the wrong limit.

  Consequence worth stating plainly: **AC-020 is now proven by physics rather than by injection.**
  The over-vibration, over-current and over-temperature trips all fire from a configured profile.

### Also in this change

- Section 17 observability counters on `EquipmentSimulation` - state ticks, protective trips by
  condition, setpoint writes by result, fault injections. Every label is an enum, so the label set is
  bounded and `equipmentId` is never one. The exporter needs a metrics library, which is a dependency
  decision, so it belongs to the Phase 2 host. `simulator_loop_overruns_total` arrives with the loop
  host for the same reason: there is no loop to overrun yet.
- `scripts/README.md` rewritten around what the directory is for. `finalize-rename.ps1` is marked
  retired - the rename completed and its path constants no longer refer to anything.

62 unit tests pass.

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
forbidden transitions, all 10 fault profiles (**11 since OD-001** - see the entry above), L3
protective trips, the 30 s dead-man revert, and
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
