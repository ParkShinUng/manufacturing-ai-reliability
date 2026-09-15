1. ID: CODEX-GAP-001
SEVERITY: BLOCKER
AREA: process
FILE(S): `MASTER_SPEC.md` section 10, `docs/08-roadmap/IMPLEMENTATION_PLAN.md`, `CHANGELOG.md`
FINDING: The repo still says implementation is blocked and the documentation gate is closed, despite the target being v0.3 “Implementation Ready.”
WHY IT BLOCKS IMPLEMENTATION: A Phase 1 engineer cannot know whether code work is authorized.
SUGGESTED RESOLUTION: Add a v0.3 readiness decision that explicitly opens or keeps closed the implementation gate with remaining blockers.

2. ID: CODEX-GAP-002
SEVERITY: BLOCKER
AREA: contracts
FILE(S): `docs/03-contracts/EVENT_CONTRACTS.md` topic `factory.telemetry.v1`, `contracts/jsonschema/v1/telemetry.schema.json`
FINDING: The telemetry Markdown example omits schema-required `measurements.voltageV`, `measurements.torqueNm`, and `measurements.operationRatePct`.
WHY IT BLOCKS IMPLEMENTATION: Producers following the example will emit events rejected by the machine-readable schema.
SUGGESTED RESOLUTION: Make the example and JSON Schema field-identical, including required measurements.

3. ID: CODEX-GAP-003
SEVERITY: HIGH
AREA: contracts
FILE(S): `docs/03-contracts/EVENT_CONTRACTS.md`, `contracts/jsonschema/v1/telemetry.schema.json`
FINDING: `quality.flags` is an unconstrained string array in schema, while safety requirements depend on stable bad-quality semantics.
WHY IT BLOCKS IMPLEMENTATION: Safety gate behavior for `SENSOR_QUALITY_BAD` cannot be implemented consistently.
SUGGESTED RESOLUTION: Define a closed or versioned set of quality flags with per-flag safety behavior.

4. ID: CODEX-GAP-004
SEVERITY: BLOCKER
AREA: contracts
FILE(S): `docs/03-contracts/EVENT_CONTRACTS.md` topic `factory.predictions.v1`, `docs/07-adr/ADR-0010-shadow-before-canary.md`
FINDING: ADR-0010 requires prediction events to identify `deploymentStage`, but the prediction event example has no `deploymentStage` field.
WHY IT BLOCKS IMPLEMENTATION: Safety Supervisor cannot enforce shadow/canary/production authority from the documented event.
SUGGESTED RESOLUTION: Add `deploymentStage` and authorized cohort semantics to the prediction contract.

5. ID: CODEX-GAP-005
SEVERITY: BLOCKER
AREA: contracts
FILE(S): `docs/03-contracts/EVENT_CONTRACTS.md`, `contracts/jsonschema/v1`
FINDING: Only telemetry has a machine-readable schema; prediction, safety-decision, command-outcome, model-deployment, and fault-injection records have no schemas.
WHY IT BLOCKS IMPLEMENTATION: Cross-language services must invent payload validation and compatibility rules.
SUGGESTED RESOLUTION: Add v1 schemas for every public event and persisted audit record.

6. ID: CODEX-GAP-006
SEVERITY: HIGH
AREA: contracts
FILE(S): `contracts/openapi/operations-api-v1.yaml`, `docs/01-requirements/FUNCTIONAL_REQUIREMENTS.md` FR-050/FR-051
FINDING: Operations API paths define no response schemas, request bodies, pagination, timestamps, error model, or demo fault-injection payload.
WHY IT BLOCKS IMPLEMENTATION: The dashboard cannot be implemented from the contract without inventing API shapes.
SUGGESTED RESOLUTION: Define OpenAPI schemas for equipment summaries, platform health, decisions, errors, and fault-injection requests.

7. ID: CODEX-GAP-007
SEVERITY: HIGH
AREA: contracts
FILE(S): `contracts/proto/control/v1/control.proto`, `docs/03-contracts/API_AND_COMMAND_CONTRACTS.md`
FINDING: The gRPC command contract uses plain strings for timestamps, source, unit, and reason code, with no enum or validation contract.
WHY IT BLOCKS IMPLEMENTATION: Control Service behavior differs depending on undocumented parsing and validation choices.
SUGGESTED RESOLUTION: Add typed enums, timestamp type guidance, allowed units/sources, and invalid-field response behavior.

8. ID: CODEX-GAP-008
SEVERITY: BLOCKER
AREA: safety
FILE(S): `docs/02-architecture/FAILURE_MODEL.md` degraded modes, `docs/04-ai/AI_SAFETY_AND_MLOPS.md` fallback policy
FINDING: Modes `NORMAL_RULE`, `AI_ASSISTED`, `SAFE_FALLBACK`, and `STOP_REQUIRED` are listed, but no state machine, transition triggers, guards, or race handling are defined.
WHY IT BLOCKS IMPLEMENTATION: Safety Supervisor and dashboard mode behavior must be invented.
SUGGESTED RESOLUTION: Define a mode/state transition table with entry/exit criteria, precedence, and audit events.

9. ID: CODEX-GAP-009
SEVERITY: BLOCKER
AREA: safety
FILE(S): `docs/04-ai/AI_SAFETY_AND_MLOPS.md` fallback policy, `docs/02-architecture/RUNTIME_BEHAVIOR.md` simulator operation-rate policy
FINDING: Fallback says “configuration-backed” and “per equipment class,” but no configuration schema, ownership, reload behavior, or invalid-config behavior exists.
WHY IT BLOCKS IMPLEMENTATION: The deterministic fallback path is not actually implementable deterministically.
SUGGESTED RESOLUTION: Add a fallback configuration contract with validation and fail-closed behavior.

10. ID: CODEX-GAP-010
SEVERITY: BLOCKER
AREA: safety
FILE(S): `docs/03-contracts/API_AND_COMMAND_CONTRACTS.md` rules, `docs/02-architecture/SECURITY_BOUNDARIES.md`
FINDING: `explicit human/manual mode` may originate production commands, but no manual-mode state, authorization, interlock, or Safety Supervisor relationship is defined.
WHY IT BLOCKS IMPLEMENTATION: This is a potential bypass around the deterministic safety path.
SUGGESTED RESOLUTION: Define manual mode as a separate controlled state with auth, audit, command limits, and conflict rules.

11. ID: CODEX-GAP-011
SEVERITY: HIGH
AREA: distributed
FILE(S): `docs/01-requirements/FUNCTIONAL_REQUIREMENTS.md` FR-011, `docs/07-adr/ADR-0001-event-driven-kafka.md`
FINDING: Replay and duplicate tolerance are required, but offset commit policy, consumer restart behavior, duplicate identity, and replay ranges are undefined.
WHY IT BLOCKS IMPLEMENTATION: Consumers must invent correctness semantics around Kafka delivery.
SUGGESTED RESOLUTION: Specify consumer offset, replay, dedupe, and restart semantics per topic.

12. ID: CODEX-GAP-012
SEVERITY: HIGH
AREA: distributed
FILE(S): `docs/03-contracts/EVENT_CONTRACTS.md` field `sequence`, `docs/07-adr/ADR-0001-event-driven-kafka.md`
FINDING: `sequence` is required, but its scope, monotonicity, reset behavior, gap handling, and relationship to Kafka partition order are undefined.
WHY IT BLOCKS IMPLEMENTATION: Ordering, replay, and stale-data detection cannot be made consistent.
SUGGESTED RESOLUTION: Define `sequence` as per-equipment or global, with reset/gap/duplicate rules.

13. ID: CODEX-GAP-013
SEVERITY: HIGH
AREA: distributed
FILE(S): `docs/02-architecture/SYSTEM_ARCHITECTURE.md` `edge-gateway`, `docs/02-architecture/FAILURE_MODEL.md` retry rule
FINDING: Edge Gateway has “bounded local buffering,” but no buffer size, eviction/drop policy, Kafka-down behavior, or backpressure behavior is specified.
WHY IT BLOCKS IMPLEMENTATION: Gateway failure behavior during Kafka outage is undefined.
SUGGESTED RESOLUTION: Specify queue capacity, overflow policy, metrics, and data-loss audit behavior.

14. ID: CODEX-GAP-014
SEVERITY: HIGH
AREA: distributed
FILE(S): `docs/02-architecture/FAILURE_MODEL.md` retry rule, `docs/06-development/CODING_STANDARDS.md`
FINDING: Retries and timeouts are required but no numeric timeout, retry count, backoff, jitter, or circuit-breaker policy is given per dependency.
WHY IT BLOCKS IMPLEMENTATION: Implementers will choose incompatible failure behavior service by service.
SUGGESTED RESOLUTION: Add retry/timeout profiles for OPC UA, Modbus, Kafka, PostgreSQL, MLflow, gRPC, and HTTP.

15. ID: CODEX-GAP-015
SEVERITY: BLOCKER
AREA: safety
FILE(S): `MASTER_SPEC.md` AI safety policy, `docs/02-architecture/FAILURE_MODEL.md` OOD telemetry row
FINDING: `MASTER_SPEC.md` says any failed AI gate rejects the recommendation, but `FAILURE_MODEL.md` says OOD may “reject or reduce AI authority per model policy.”
WHY IT BLOCKS IMPLEMENTATION: OOD behavior is contradictory on a safety-critical path.
SUGGESTED RESOLUTION: Pick one OOD behavior for v0.3 and document any reduced-authority mode explicitly.

16. ID: CODEX-GAP-016
SEVERITY: BLOCKER
AREA: safety
FILE(S): `docs/04-ai/AI_SAFETY_AND_MLOPS.md` safety gate order, `docs/08-roadmap/OPEN_DECISIONS.md` OOD baseline
FINDING: OOD threshold, confidence threshold, required feature completeness, and equipment eligibility rules are named but not numerically or logically defined.
WHY IT BLOCKS IMPLEMENTATION: Safety Supervisor gates cannot be tested or reproduced.
SUGGESTED RESOLUTION: Add threshold/config contracts and per-gate acceptance tests.

17. ID: CODEX-GAP-017
SEVERITY: HIGH
AREA: safety
FILE(S): `docs/02-architecture/SYSTEM_ARCHITECTURE.md` `safety-supervisor`, `docs/03-contracts/EVENT_CONTRACTS.md`
FINDING: Safety Supervisor consumes predictions plus “required equipment state,” but no equipment-state event/API contract exists.
WHY IT BLOCKS IMPLEMENTATION: The service must invent where current state comes from and how fresh it must be.
SUGGESTED RESOLUTION: Define equipment-state source, schema, freshness rules, and conflict behavior.

18. ID: CODEX-GAP-018
SEVERITY: HIGH
AREA: safety
FILE(S): `docs/03-contracts/EVENT_CONTRACTS.md` `factory.predictions.v1`, `docs/03-contracts/API_AND_COMMAND_CONTRACTS.md`
FINDING: There are no rules for stale, duplicate, reordered, or superseded predictions producing commands after a newer decision exists.
WHY IT BLOCKS IMPLEMENTATION: Race conditions between prediction cadence and control command execution are unresolved.
SUGGESTED RESOLUTION: Add per-equipment decision ordering, supersession, command TTL, and cancellation semantics.

19. ID: CODEX-GAP-019
SEVERITY: HIGH
AREA: requirements
FILE(S): `docs/02-architecture/RUNTIME_BEHAVIOR.md`, `docs/08-roadmap/IMPLEMENTATION_PLAN.md` Phase 1
FINDING: Phase 1 requires a simulator state machine and fault injection, but equipment states, state transitions, sensor ranges, fault equations, and stop conditions are not specified.
WHY IT BLOCKS IMPLEMENTATION: The simulator would be mostly invented in code.
SUGGESTED RESOLUTION: Add an equipment model/state-machine spec with ranges, units, dynamics, and fault profiles.

20. ID: CODEX-GAP-020
SEVERITY: HIGH
AREA: contracts
FILE(S): `docs/01-requirements/FUNCTIONAL_REQUIREMENTS.md` FR-003, `docs/08-roadmap/IMPLEMENTATION_PLAN.md` Phase 1
FINDING: OPC UA and Modbus TCP are required, but no node map, register map, data type mapping, endian/scaling rules, or write address map is defined.
WHY IT BLOCKS IMPLEMENTATION: Protocol endpoints and gateway normalization cannot be built interoperably.
SUGGESTED RESOLUTION: Add OPC UA namespace and Modbus register mapping contracts.

21. ID: CODEX-GAP-021
SEVERITY: HIGH
AREA: time
FILE(S): `docs/03-contracts/EVENT_CONTRACTS.md` `eventTimeUtc`/`ingestTimeUtc`, `docs/02-architecture/RUNTIME_BEHAVIOR.md` feature timing
FINDING: UTC timestamps are required, but clock source, skew tolerance, event-time vs ingest-time aggregation, and freshness calculation are undefined.
WHY IT BLOCKS IMPLEMENTATION: Stale-data and feature-window behavior will diverge under clock skew or replay.
SUGGESTED RESOLUTION: Define time authority, skew limits, and which timestamp drives each gate/aggregation.

22. ID: CODEX-GAP-022
SEVERITY: HIGH
AREA: mlops
FILE(S): `docs/04-ai/AI_SAFETY_AND_MLOPS.md` MLflow lifecycle, `docs/02-architecture/FAILURE_MODEL.md` MLflow unavailable row
FINDING: Runtime model metadata caching is required, but cache contents, validation, expiry, startup behavior, and stale-model failure behavior are undefined.
WHY IT BLOCKS IMPLEMENTATION: Inference behavior during MLflow outage is not safely specified.
SUGGESTED RESOLUTION: Define model metadata cache contract, startup policy, expiry policy, and quarantine propagation.

23. ID: CODEX-GAP-023
SEVERITY: HIGH
AREA: mlops
FILE(S): `docs/04-ai/AI_SAFETY_AND_MLOPS.md` promotion gate, `docs/07-adr/ADR-0010-shadow-before-canary.md`
FINDING: Promotion checks are listed qualitatively, but no metric thresholds, approval owner, rollback trigger, or cohort authorization contract exists.
WHY IT BLOCKS IMPLEMENTATION: Unsafe model promotion remains a process gap hidden behind vague words.
SUGGESTED RESOLUTION: Add quantitative promotion/rollback gates and an auditable model-deployment contract.

24. ID: CODEX-GAP-024
SEVERITY: MEDIUM
AREA: architecture
FILE(S): `docs/02-architecture/RUNTIME_BEHAVIOR.md` persistence, `docs/02-architecture/SYSTEM_ARCHITECTURE.md` `operations-service`
FINDING: PostgreSQL ownership is named, but table/read-model schemas, retention, compaction, migration ownership, and rebuild-from-Kafka behavior are not defined.
WHY IT BLOCKS IMPLEMENTATION: Operations Service projections and trace retrieval must invent storage contracts.
SUGGESTED RESOLUTION: Add persistence/read-model contracts and migration/rebuild rules.

25. ID: CODEX-GAP-025
SEVERITY: HIGH
AREA: security
FILE(S): `docs/02-architecture/SECURITY_BOUNDARIES.md`, `contracts/openapi/operations-api-v1.yaml`, `contracts/proto/control/v1/control.proto`
FINDING: Security boundaries are prose only; no local service identity, API authentication, gRPC authorization, demo endpoint guard, or credential rotation contract exists.
WHY IT BLOCKS IMPLEMENTATION: The equipment-write and fault-injection surfaces are not protected by implementable controls.
SUGGESTED RESOLUTION: Define authn/authz mechanisms per boundary, including local demo and cloud profiles.

26. ID: CODEX-GAP-026
SEVERITY: MEDIUM
AREA: observability
FILE(S): `docs/05-operations/OBSERVABILITY_AND_SLO.md`
FINDING: Metrics are named but lack types, units, label cardinality, service ownership, alert thresholds, and SLO measurement windows.
WHY IT BLOCKS IMPLEMENTATION: Teams cannot produce comparable SLO evidence or dashboards.
SUGGESTED RESOLUTION: Add a metrics contract with type, labels, units, scrape source, and acceptance thresholds.

27. ID: CODEX-GAP-027
SEVERITY: HIGH
AREA: testing
FILE(S): `docs/01-requirements/ACCEPTANCE_CRITERIA.md`, `docs/01-requirements/FUNCTIONAL_REQUIREMENTS.md`
FINDING: Many P0 requirements lack direct acceptance coverage, including FR-002, FR-003, FR-005, FR-010, FR-012, FR-020, FR-021, FR-022, FR-030, FR-035, and FR-052.
WHY IT BLOCKS IMPLEMENTATION: Phase 1 cannot prove compliance against the requirement set.
SUGGESTED RESOLUTION: Add AC IDs or explicit deferrals for every P0 FR/NFR.

28. ID: CODEX-GAP-028
SEVERITY: MEDIUM
AREA: testing
FILE(S): `docs/01-requirements/ACCEPTANCE_CRITERIA.md` AC-001, `docs/01-requirements/FUNCTIONAL_REQUIREMENTS.md` FR-001
FINDING: FR-001 requires 20 demo equipment instances, but AC-001 only requires 10+ simulated machines.
WHY IT BLOCKS IMPLEMENTATION: A system can pass acceptance while failing the P0 demo-scale requirement.
SUGGESTED RESOLUTION: Align AC-001 with 20 demo instances or explicitly change FR-001.

29. ID: CODEX-GAP-029
SEVERITY: MEDIUM
AREA: testing
FILE(S): `docs/05-operations/RUNBOOK_AND_FAILURE_TESTS.md`
FINDING: Failure tests list scenarios but not exact setup, fault trigger, expected state transitions, metric thresholds, or pass/fail assertions.
WHY IT BLOCKS IMPLEMENTATION: Reproducible failure evidence cannot be produced from the runbook.
SUGGESTED RESOLUTION: Convert each failure test into an executable test spec with inputs, timing, expected outputs, and metrics.

30. ID: CODEX-GAP-030
SEVERITY: HIGH
AREA: contracts
FILE(S): `docs/02-architecture/REPOSITORY_STRUCTURE.md` contract ownership, `MASTER_SPEC.md` source-of-truth order
FINDING: Repository Structure says `contracts/` contains machine-readable source-of-truth schemas, but `MASTER_SPEC.md` ranks docs/03 contracts above machine contracts.
WHY IT BLOCKS IMPLEMENTATION: When prose and schema conflict, the precedence rule is ambiguous.
SUGGESTED RESOLUTION: Define a single contract precedence rule and require generated examples from schemas.

**TOP 10 THINGS THAT WOULD STOP PHASE 1 (ranked)**

1. Missing simulator state machine and fault equations: CODEX-GAP-019.
2. Missing OPC UA/Modbus node/register mapping: CODEX-GAP-020.
3. Telemetry Markdown/schema mismatch: CODEX-GAP-002.
4. Missing data-quality flag semantics: CODEX-GAP-003.
5. Undefined edge buffering/backpressure during Kafka outage: CODEX-GAP-013.
6. Undefined sequence/order/replay semantics: CODEX-GAP-011 and CODEX-GAP-012.
7. Documentation gate still closed: CODEX-GAP-001.
8. Acceptance criteria do not cover Phase 1 requirements: CODEX-GAP-027 and CODEX-GAP-028.
9. Missing exact failure-test specs: CODEX-GAP-029.
10. Ambiguous contract source-of-truth precedence: CODEX-GAP-030.

**WHAT A SENIOR MANUFACTURING/OT ENGINEER WOULD CHALLENGE**

The repo asks for OPC UA and Modbus TCP but provides no node/register maps, scaling, endian rules, quality mapping, command write addresses, or equipment state model. They would also challenge the phrase “STOP_REQUIRED” because no stop condition, permissive state, or manual-mode interlock is specified.

**WHAT A DISTRIBUTED SYSTEMS ENGINEER WOULD CHALLENGE**

Kafka is selected, but offset commits, replay windows, duplicate identity, ordering by `equipmentId`, sequence gaps, buffering overflow, DLQ behavior, and restart semantics are not specified. They would also challenge “bounded retry” because no timeout, retry count, or backoff profile exists.

**WHAT AN MLOPS ENGINEER WOULD CHALLENGE**

MLflow is chosen, but runtime model cache validity, feature-schema compatibility, stale model behavior, promotion thresholds, rollback triggers, and `deploymentStage` contract support are incomplete. Candidate/shadow/canary authority is asserted in ADR-0010 but not implementable from the current prediction event.

Codex session ID: 01a0a260-428c-7301-a095-6f9e59eeecec
Resume in Codex: codex resume 01a0a260-428c-7301-a095-6f9e59eeecec
