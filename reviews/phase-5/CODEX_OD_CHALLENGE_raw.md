# Codex challenge of OD-024 to OD-029

> Raw output, verbatim, `codex exec -s read-only`, 2026-10-07. OD-024..028 SOUND_WITH_CONDITIONS
> (A); OD-029 WRONG_OPTION; four further findings, three P1.

Read-only challenge completed. I did not modify files.

**OD-024**
Verdict: `SOUND_WITH_CONDITIONS`  
Claude recommendation: A.  
Recommended option: A.

Conditions:
1. The Python consumer must be tested against the same conformance fixtures as C# `ContractConsumer`: invalid JSON, schema invalid, handler failure, tombstone allowed/forbidden, outage, commit-after-settle, DLQ headers.
2. Fixtures must assert commit position and “downstream handler not called” for first-attempt DLQ cases.
3. Python must consume topic metadata, not hard-code topic names, for tombstone allowance and DLQ behavior.
4. Phase 5 must not weaken the Phase 6 Supervisor exception: reject first, then DLQ.

Reasoning: OD-009’s real invariant is one rule set, not one binary. `ContractConsumer` is C#, while Phase 5 is Python, so a second implementation is unavoidable unless scope is moved. A conformance suite is the right control, provided it is mandatory CI, not advisory. Supported by `OPEN_DECISIONS.md:1325`, `ContractConsumer.cs:18`, `KAFKA_TOPOLOGY_AND_SEMANTICS.md:198`.

**OD-025**
Verdict: `SOUND_WITH_CONDITIONS`  
Claude recommendation: A.  
Recommended option: A.

Conditions:
1. Dataset manifest must be a contracted artifact: fleet config, seed, profile, `T_fail`, duration, generator git commit, schema versions, and SHA-256 of canonical telemetry bytes.
2. Generated telemetry must validate against `telemetry.schema.json` after the same normalisation path Phase 5 claims to train on.
3. Labels must be explicitly “manifest labels,” not `factory.faults.v1` labels.
4. AC-008 must record dataset hash/version in MLflow lineage.

Reasoning: This correctly avoids inventing `factory.faults.v1` before Phase 11 and avoids a 24 h dataset depending on 6 h Kafka retention. Simulator determinism is real: fixed seed and command sequence are designed as a pure function, and tests prove bit-identical output. Supported by `OPEN_DECISIONS.md:1351`, `EQUIPMENT_MODEL_AND_STATE.md:155`, `DeterminismTests.cs:40`.

**OD-026**
Verdict: `SOUND_WITH_CONDITIONS`  
Claude recommendation: A.  
Recommended option: A.

Conditions:
1. Rename Phase 5 wording from “authorized model” to “configured SHADOW model” wherever it describes runtime loading.
2. Any `deploymentStage` other than `SHADOW` must fail startup in Phase 5.
3. Phase 5 must not consume or produce `factory.model-deployments.v1`.
4. AC-008 proof is traceability only: prediction model fields resolve to MLflow run, dataset hash, feature schema version, and git commit.

Reasoning: Authorization is Phase 9 via `mlops-publisher`; Phase 5 cannot honestly claim it. SHADOW is compatible with ADR-0010 because it has no control authority. Supported by `OPEN_DECISIONS.md:1380`, `IMPLEMENTATION_PLAN.md:258`, `ADR-0019-authorization-via-compacted-topic.md:20`.

**OD-027**
Verdict: `SOUND_WITH_CONDITIONS`  
Claude recommendation: A.  
Recommended option: A, amended.

Conditions:
1. `predictionId` UUIDv5 input must include `model.runId` or the docs must prove `modelName + modelVersion` is globally unique for one artifact.
2. UUIDv5 namespace IDs and canonical string encoding must be contracted; no ad hoc concatenation.
3. `eventId` semantics must be stated: deterministic with `predictionId`, or intentionally per-emission and ignored for duplicate identity.
4. Schema prose must stop saying strict `predictedAtUtc` is “required by DEC-003” without noting DEC-003 made it secondary, not primary.

Reasoning: Derived IDs are the right answer for restart/replay and align with duplicate identity `predictionId`. But DEC-003 explicitly rejects wall-clock ordering as primary, while the schema still overstates it. Shadow/canary identity is mostly handled by `deploymentStage`, but artifact identity should include `runId` unless version uniqueness is guaranteed. Supported by `OPEN_DECISIONS.md:1414`, `prediction.schema.json:37`, `DEC-003-command-ordering.md:51`.

**OD-028**
Verdict: `SOUND_WITH_CONDITIONS`  
Claude recommendation: A.  
Recommended option: A.

Conditions:
1. No inference may use a window until all included 1 s buckets are final under the same event-time/partition-order rule.
2. Restart recovery must replay records through the same finalization/drop-late logic, not rebuild by sorting event time.
3. Late-drop counters must be part of test assertions, not only observability.
4. Duplicate handling must account for equipment restart/epoch reset; `(equipmentId, sequence)` alone is unsafe.

Reasoning: The feature vector and canonical byte comparison close the AC-032 gap. The late-record rule can be restart-invariant only if live and rebuild process identical ordered streams. The sequence-only dedupe rule conflicts with documented sequence reset behavior. Supported by `OPEN_DECISIONS.md:1437`, `TIME_AND_DATA_QUALITY.md:156`, `EDGE_GATEWAY.md:187`.

**OD-029**
Verdict: `WRONG_OPTION`  
Claude recommendation: A.  
Recommended option: A-prime: define every output, but do not derive `confidence` from `oodScore`.

Conditions:
1. `confidence` must be an independent calibrated model-output quality measure, not `1 - oodScore * 2`.
2. Gate 8 OOD and gate 9 confidence must remain separately testable and separately tunable.
3. `oodScore` scale and envelope math must define zero-MAD behavior, missing/advisory feature behavior, and clipping.
4. `recommendedOperationRatePct` must define behavior when `failureProbability` is below threshold, not only a linear formula.

Reasoning: The problem is real: schema outputs are underdefined. But coupling confidence to OOD collapses gates 8 and 9, making confidence threshold a disguised OOD threshold. That undermines the canonical gate table’s separate OOD and confidence checks. Supported by `OPEN_DECISIONS.md:1462`, `AI_SAFETY_AND_MLOPS.md:39`, `AI_SAFETY_AND_MLOPS.md:40`.

**Other Findings**

`P5-ODC-001` — Severity: P1  
`docs/03-contracts/KAFKA_TOPOLOGY_AND_SEMANTICS.md:171`, `contracts/jsonschema/v1/telemetry.schema.json:57`, `docs/11-service-design/EDGE_GATEWAY.md:187`  
Defect: Telemetry duplicate identity is `(equipmentId, sequence)`, but sequence resets to 0 on equipment restart when `sourceEpochMs` also resets. OD-028 repeats the unsafe identity for feature dedupe.  
Fix: Include source epoch/restart discriminator in telemetry duplicate identity and feature dedupe, or prove the gateway rewrites sequence into a monotonic epoch-safe identity.

`P5-ODC-002` — Severity: P1  
`contracts/jsonschema/v1/prediction.schema.json:37`, `docs/09-decisions/DEC-003-command-ordering.md:51`  
Defect: Prediction schema says strict `predictedAtUtc` is required by DEC-003, but DEC-003 says timestamp ordering is secondary defense-in-depth, not primary correctness.  
Fix: Amend schema description and Phase 5 design so strict monotonic timestamp is a fail-safe guard, while command safety still depends on TTL/cadence/epoch fencing.

`P5-ODC-003` — Severity: P1  
`docs/08-roadmap/OPEN_DECISIONS.md:1437`, `src/dotnet/EquipmentSimulator.Tests/DeterminismTests.cs:88`  
Defect: OD-028’s duplicate rule would drop valid post-restart samples if sequence restarts at 0 and old samples remain in a 60 s feature horizon.  
Fix: Make feature dedupe use the corrected telemetry duplicate identity from `P5-ODC-001`.

`P5-ODC-004` — Severity: P2  
`docs/08-roadmap/OPEN_DECISIONS.md:1414`, `contracts/jsonschema/v1/prediction.schema.json:49`  
Defect: OD-027 derives `predictionId` from model name/version/stage but omits `runId`, while the contract treats run ID as the MLflow artifact link.  
Fix: Include `runId` in `predictionId` derivation or document an invariant that model version uniquely identifies one run forever.

**Agreement / Product Owner Choice**

I agree with Claude’s Option A on OD-024, OD-025, OD-026, OD-027, and OD-028, subject to the conditions above.

OD-029 needs product-owner choice between Claude’s coupled-confidence Option A and the challenger position: define all outputs now, but keep confidence independent from OOD so gates 8 and 9 remain separate safety gates.
