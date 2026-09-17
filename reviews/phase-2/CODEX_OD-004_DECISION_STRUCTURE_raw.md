# Codex follow-up - how to frame OD-004 for the product owner

> Raw output from the `codex exec resume` run of 2026-09-16, verbatim; same session as
> [`CODEX_OD-004_ANALYSIS_raw.md`](CODEX_OD-004_ANALYSIS_raw.md).
>
> Asked because the two findings were entangled: deciding them separately risked one answer making
> the other meaningless. The conclusion - one decision package, two recorded sub-decisions - is what
> `OPEN_DECISIONS.md` OD-004 follows.

This should be framed as **one decision package with two sub-decisions**, not one single decision.

(A) and (B) are coupled because adding `SourceEpochMs` to OPC UA makes the same restart/gap rule evaluable on both protocols. But (A) does **not** force the answer to (B). You can add the missing OPC UA signal and still choose either semantic: “epoch reset is a gap” or “epoch reset is a restart boundary, not a gap.”

My recommendation: **resolve both together** so the product owner answers once, but record them as two explicit decisions.

**Decision 1: Protocol Parity**
Add a per-equipment OPC UA `SourceEpochMs` read-only node matching Modbus `sourceEpochMs`.

Reason: without it, OPC UA cannot implement the documented restart-versus-gap rule. `SourceTimestamp` and OPC UA `ServerStatus.StartTime` are not substitutes.

**Decision 2: Epoch Reset Semantics**
Choose the `EDGE_GATEWAY.md` / normaliser interpretation: when `sequence` goes backward **and** `sourceEpochMs` also goes backward, treat it as a restart or epoch-wrap boundary, **not** `SEQUENCE_GAP`; restart sequence tracking.

So: `TIME_AND_DATA_QUALITY.md` is the wrong document here.

The safety argument:

- False `SEQUENCE_GAP` on every legitimate restart/wrap is noisy and degrades trustworthy restart recovery into an apparent data-loss incident. It makes `telemetry_sequence_gaps_total` lie and can push canonical quality to `UNCERTAIN` for a condition the contract already says is distinguishable.
- A real discontinuity hidden behind the restart rule is worse in principle, but the mitigation is the paired-signal invariant: only suppress `SEQUENCE_GAP` when both `sequence` and `sourceEpochMs` reset together. If sequence goes backward alone, flag it. If sequence jumps forward by more than 1, flag it. If epoch goes backward alone, that should be treated as an inconsistent-source condition, not silently accepted.

The subtle point: a restart is a discontinuity, but it is not the same thing as a dropped telemetry sample within one sequence epoch. AC-023 should mean “no gap inside a continuous source epoch goes undetected,” not “every restart is reported as a gap.”

This also affects the Phase 1 simulator contract around `2^32 ms` wrap. The simulator behavior where `sequence` and `sourceEpochMs` reset together is still correct. The affected component is the gateway interpretation. If `TIME_AND_DATA_QUALITY.md` won, then every 49.7-day wrap would become a `SEQUENCE_GAP`; I think that is the wrong semantic.

**Docs To Change**

- `OT_PROTOCOL_MAPPING.md` section 1.2: add OPC UA read-only `SourceEpochMs`, `UInt32`, maps to the same source epoch concept as Modbus.
- `OT_PROTOCOL_MAPPING.md` section 1.5 or nearby: state OPC UA `SourceTimestamp` remains `eventTimeUtc`; it is not restart identity.
- `EDGE_GATEWAY.md` section 14: keep the current rule, but make it precise: sequence rollback plus epoch rollback is restart/wrap boundary, not `SEQUENCE_GAP`; sequence rollback without epoch rollback is `SEQUENCE_GAP`.
- `TIME_AND_DATA_QUALITY.md` section 4: replace “equipment clock reset raises `SEQUENCE_GAP`” with “equipment epoch reset restarts sequence tracking when paired with sequence reset; unpaired/inconsistent reset is flagged.”
- `ACCEPTANCE_CRITERIA.md` AC-023: clarify “no dropped sample within a continuous source epoch goes undetected.”
- Tests/specs: add paired cases for Modbus and OPC UA: forward gap, duplicate, sequence rollback alone, epoch rollback alone, and paired sequence+epoch rollback.

I would avoid adding a new telemetry quality flag unless the product owner explicitly wants restart visibility in the event stream, because the flag vocabulary is schema-closed and that becomes a schema-versioning decision.
