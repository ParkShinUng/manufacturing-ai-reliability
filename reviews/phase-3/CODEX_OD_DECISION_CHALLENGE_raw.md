# Codex challenge - the intended OD-007 / OD-008 / OD-009 decisions

> Raw output, transcribed verbatim from the `codex exec` run of 2026-09-28 (read-only), which
> challenged the decisions before they were recorded. All three: SOUND_WITH_CONDITIONS.

Q1 - P1 - docs/08-roadmap/OPEN_DECISIONS.md:OD-007 - Phase 3 “replay mechanics” is only provable if it gets a real Phase 3 replay harness, not a placeholder projector claim - otherwise the split moves the same hole into smaller words - Phase 3 test must assert a replay-eligible consumer group can be manually rewound over a known offset range, consumes exactly the expected records in key/order, commits offsets only after successful processing, and that `cg.safety-supervisor.v1` remains non-replayable by configuration/startup policy.

Q2 - P0 - docs/02-architecture/FAILURE_MODEL.md:72 - Splitting AC-003 weakens F11 unless one later criterion still owns the whole “replay cannot move equipment” safety claim - F11 is the unacceptable consequence, not three independent implementation facts - keep an explicit final replay-safety acceptance test in the first phase where Supervisor command intent can be observed, and later Control Service/equipment movement can be fenced.

Q3 - P1 - tests/contract/ac_traceability.mjs:29 - Lettered `AC-003a/b/c` parts are invisible to the current traceability parser, while §7a is phase-based by `AC-\d{3}` - this would let Phase 3 cover “AC-003” and hide later unproven parts - use new numeric AC IDs, or update the checker before using lettered parts; with the current checker, new `AC-nnn` IDs are the honest choice.

Q4 - P0 - docs/11-service-design/SAFETY_SUPERVISOR.md:119 - OD-008 leaves the refresh interval unset while gate 10 fails on state stale `> 10 s` - a healthy stable machine can still fall back forever if refresh plus latency plus clock skew exceeds the gate - specify the constraint as `stateRefreshInterval + worst-case produce/consume latency + clockSkewBudget < 10 s`, with `clockSkewBudget` currently 250 ms from docs/02-architecture/TIME_AND_DATA_QUALITY.md:35; also verify at LOAD-002 scale, 250 equipment, because traffic is `250 / interval` state records per second.

Q5 - P1 - contracts/jsonschema/v1/equipment-state.schema.json:7 - `gatewayEpoch` is not just a duplicate-identity prose tweak: the schema is `additionalProperties:false`, so every producer/consumer contract must change - compaction by Kafka key `equipmentId` is unaffected, but consumer dedupe semantics change from `(equipmentId,stateSequence)` to `(equipmentId,gatewayEpoch,stateSequence)` - define epoch type and invariants: stable for one gateway counter epoch, changes whenever `stateSequence` can reset, unique across restarts with collision probability treated as impossible for the demo, and not interpreted as event ordering.

Q6 - P1 - docs/03-contracts/KAFKA_TOPOLOGY_AND_SEMANTICS.md:25 - `compact` only fixes accidental aging-out, but creates an unnamed cost: decommissioned equipment never disappears - without a tombstone/removal rule, startup state and configured equipment inventory can drift permanently - add a tombstone/decommission contract and name its writer; `delete` was load-bearing only for automatic aging/storage, but it was unsafe for “current state must be learnable.”

Q7 - P1 - contracts/jsonschema/v1/equipment-state.schema.json:103 - Refresh records with no state change have undefined time semantics - if `occurredAtUtc` remains the original transition time, periodic refresh does not satisfy gate 10; if it is observation time, transition history needs `transitionId/previousState` semantics to stay clear - define refresh as a new observation with fresh `occurredAtUtc` and `ingestTimeUtc`, and make gate 10 measure observation age against wall-clock plus skew, not original transition age.

Q8 - P1 - docs/03-contracts/KAFKA_TOPOLOGY_AND_SEMANTICS.md:188 - OD-009 option A is only non-speculative if the “shared component” is the production consumer path, not a framework invented ahead of use - otherwise B is cleaner and AC-027 should move to Phase 4 - constrain A to a minimal library/package used by every later consumer: schema validate, first-attempt DLQ for invalid schema, required headers, manual commit after DLQ produce, no automatic redrive.

Q9 - P1 - docs/01-requirements/ACCEPTANCE_CRITERIA.md:64 - A test-only consumer proves AC-027 only if it executes the same production component and the repo later enforces adoption by real consumers - otherwise it demonstrates a toy path - the Phase 3 test consumer must consume a real Kafka topic, receive an invalid record, validate against the authoritative JSON Schema, produce the original payload to `<topic>.dlq` with all required headers before committing the source offset, prove attempt count is first-attempt/no retry, and prove downstream processing did not run.

Q10 - P1 - docs/07-adr/ADR-0017-machine-contracts-authoritative.md:20 - These decisions do not inherently contradict ADR-0017, ADR-0019, DEC-007, or the Phase 2 egress boundary, but they leave compatibility work unstated - ADR-0017/DEC-007 mean schema changes are authoritative; ADR-0019 shows the compacted-topic pattern already needs infinite retention plus liveness/watermark semantics; Phase 2’s egress boundary allows Phase 3 binding, but equipment-state production is more than “attach Kafka” - update schemas, examples, topology, test specs, and implementation plan together before Phase 3 starts.

OD-007: SOUND_WITH_CONDITIONS - split it, but use traceable numeric ACs or upgrade the checker, and keep an explicit final F11 owner.

OD-008: SOUND_WITH_CONDITIONS - option B is the right shape, but refresh timing, epoch invariants, tombstones, and refresh timestamp semantics must be specified numerically/contractually.

OD-009: SOUND_WITH_CONDITIONS - option A is justified only as a minimal production consume-validate-DLQ component with mandatory later adoption, not as a test-only abstraction.
