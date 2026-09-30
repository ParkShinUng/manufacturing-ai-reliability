# Phase 4 open decisions — challenge request to Codex

> Read-only challenge of `OD-011` to `OD-018` (`docs/08-roadmap/OPEN_DECISIONS.md`, end of file),
> written 2026-09-30 from the Phase 4 Definition-of-Ready check (`CODEX_DOR_CHALLENGE_raw.md`). The
> product owner asked for every OD to be discussed with Codex and the recommendations shown again
> **before** deciding. Nothing is decided yet.

## Claude's recommendations, to be challenged

| OD | Recommendation |
|---|---|
| 011 | A — split like OD-007: Phase 4 proves store-and-query on schema-valid records under new AC IDs; AC-006 keeps the live-chain claim and moves to Phase 7; AC-029 stays Phase 4 |
| 012 | A — later-phase and never-observed fields become nullable, `null` = "no record observed" only |
| 013 | A — the chain starts at the prediction; the trace's telemetry link is the window reference plus that range's 1 s aggregates |
| 014 | A — canonical-dump comparison; no non-deterministic columns; rebuild replaces rows only within the replayed offset range |
| 015 | A — one aggregate definition, shared conformance fixture, event-time lateness (5 s) so replay is deterministic; B if "latest" suffices |
| 016 | A — one schema per owner, each owner migrates its own; Phase 4 creates `operations` only |
| 017 | A — ES256, verifier holds public keys only, repo script mints local/demo tokens from an uncommitted key |
| 018 | the proposed table: caught-up-once readiness, staleness = time since last caught up, 20 rps/burst 40 per subject, daily partitions for 30-day tables |

## What to challenge, for each OD

1. Is the problem stated correctly, and is anything missing from it? Verify factual claims against
   the repo (e.g. OD-013's per-record `correlationId` in `src/dotnet/EdgeGateway/TelemetryNormaliser.cs`,
   OD-014's retention table against `KAFKA_TOPOLOGY_AND_SEMANTICS.md` §2 and `OPERATIONAL_DATA.md` §15).
2. Does the recommended option actually close the gap, or only appear to? Simpler alternative,
   hidden coupling, restart/replay holes, testability, contract problems, safety or trust-boundary
   effects.
3. Does it conflict with another OD, an ADR, a DEC, or an earlier resolved OD (especially OD-007,
   OD-009, OD-010, ADR-0016, ADR-0017, ADR-0018, DEC-002)?
4. Your own recommendation: the same option, the same with conditions, or a different one — and why.

## Output format

Per OD, one block:

`OD-0nn · verdict SOUND / SOUND_WITH_CONDITIONS / WRONG_OPTION / PROBLEM_MISSTATED · recommended
option · conditions (numbered, each concrete) · reasoning in at most five lines`

Then any finding outside the eight ODs as `P4-ODC-001…` with severity, file:line, defect, fix. End
with one line: which ODs you and Claude agree on, and which need the product owner to choose between
two positions.

Do not modify any file. The product owner decides.
