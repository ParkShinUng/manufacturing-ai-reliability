# Phase 5 Definition-of-Ready — challenge request to Codex

> Read-only challenge, before any Phase 5 work and before any option is drafted. Same order as
> Phases 3 and 4: questions first, options after. The product owner asked for Phase 5 on
> **2026-10-07**; the request authorises the phase, it does not make it ready.

## What Phase 5 is

`IMPLEMENTATION_PLAN.md`: reproducible dataset generation · `mair_ml_core` shared features ·
anomaly and failure/RUL baselines · MLflow experiments and registry · prediction service with
`deploymentStage` and `validSampleRatio` · deterministic null aggregation. Proof: **AC-008, AC-024,
AC-025, AC-032**. Service designs: `PREDICTION_SERVICE.md`, `AI_TRAINING_PIPELINE.md`,
`MODEL_LIFECYCLE.md`; `docs/04-ai/AI_SAFETY_AND_MLOPS.md`; `TIME_AND_DATA_QUALITY.md` §8; ADR-0003,
ADR-0008, ADR-0010, ADR-0015, ADR-0019. This is the first Python in the repository.

## Already found — challenge only the classification

- **No ADR selects any Python dependency**: environment and lock tooling, the Kafka client, JSON
  Schema validation, the ML libraries, the MLflow server runtime, the test runner. The licence gate
  (`scripts/license-scan.mjs`) reads NuGet only. CI has no Python job.
- **AC-008, AC-024, AC-025, AC-032 have no test specification** (`TEST_SPECIFICATIONS.md` §7a).

## What to challenge

1. **`OD-009` and a Python consumer.** OD-009 made the C# `ContractConsumer` the one consume path and
   named the feature builder among the consumers that must adopt it. The prediction service is
   Python. Is that a contradiction, and what has to be decided — a Python port of the same rules, a
   different boundary, or something else? What does each cost against OD-009's reason (two consumers
   disagreeing about "invalid")?
2. **Ground truth.** Training labels come from fault-injection metadata (`T_fail`, simulator).
   `factory.faults.v1` has no contract and was deferred to Phase 11 (`OD-020`). Where can Phase 5
   honestly get labels, and is AC-008's "traceable to its training run" provable without them?
3. **The dataset.** "Replay a telemetry range" against a 6 h telemetry retention, a 24 h dataset in
   the performance target, and NFR-005's reproducibility. Which is wrong, and what does a
   reproducible dataset actually reference?
4. **Authorization and deployment.** The prediction service "loads authorized models" and stamps
   `deploymentStage`; authorization is the `mlops-publisher`'s compacted topic (ADR-0019). Which phase
   builds the publisher, and what can Phase 5's service legitimately load before it exists? Is AC-008's
   "deployed prediction" provable in Phase 5?
5. **Identity and determinism of predictions.** `predictionId` is the topic's duplicate identity;
   `windowId` is the causation; `predictedAtUtc` must be strictly increasing (DEC-003). Are these
   deterministic across a restart or a replay, and what breaks if they are not (duplicates, the
   operations projection's first-wins, OD-013's chain)?
6. **AC-032's "byte-identical features"**: what representation, and between which two paths when both
   are Python? Does anything in `TIME_AND_DATA_QUALITY.md` §8 or `PREDICTION_SERVICE.md` §10 leave the
   aggregation underspecified (late records, window alignment, float formatting, the expected count
   when cadence is not exactly 10 Hz)?
7. **Model choices**: "anomaly and failure/RUL baselines" — is anything about the algorithms, the
   promotion gates or OOD (ADR-0015) left for an implementer to invent?
8. **Anything else** in the Phase 5 contracts that cannot be implemented as written — the
   prediction schema, `featureSchemaVersion`, the shadow consumer group (ADR-0010), observability names.

## Output format

`P5-DOR-nnn · severity (P0/P1/P2/P3) · file:line · what is wrong or unanswerable · why it matters ·
what must be decided or fixed before Phase 5 starts`, then **READY** or **NOT READY** with the
blocking IDs. Do not propose an implementation; where a decision is needed, say what it costs.
