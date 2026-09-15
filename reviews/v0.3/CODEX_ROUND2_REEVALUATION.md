PART A — RE-EVALUATION

DEC-001  
ROUND2_VERDICT: STILL_OPEN  
REASON: `controlEpoch` is named but not contracted: `contracts/proto/control/v1/control.proto` has no `control_epoch`, no `AcquireControlLease` RPC, no lease holder, no TTL, and no CAS/transaction semantics. `CONTROL_MODE_STATE_MACHINE.md` §7 persists mode in PostgreSQL but never says `controlEpoch` is durably persisted before fallback writes, so Control Service restart can reintroduce the stale-command race.  
REMAINING_ACTION: Define durable per-equipment epoch/lease storage, atomic acquire semantics, restart behavior, proto fields/RPCs, and reject-on-epoch-mismatch rules.

DEC-002  
ROUND2_VERDICT: STILL_OPEN  
REASON: Claude claims explicit `seekToEnd`, but `KAFKA_TOPOLOGY_AND_SEMANTICS.md` §6 still says consumer restart resumes from last committed offset, while §5 only says `auto.offset.reset=latest`. That does not implement the startup discard behavior Claude relies on.  
REMAINING_ACTION: Add Supervisor-specific startup/restart offset procedure and define interaction with control lease/fallback while waiting for the next fresh prediction.

DEC-003  
ROUND2_VERDICT: NEW_PROBLEM_INTRODUCED  
REASON: Ordering now depends on strictly monotonic `predictedAtUtc`, but `TIME_AND_DATA_QUALITY.md` §1 says it is produced by the prediction host wall clock and §2 allows wall-clock use for TTL; no per-equipment monotonic allocator survives restart, rebalance, duplicate producer, or clock step backward. Kafka partitioning orders records after production; it does not make producer timestamps monotonic.  
REMAINING_ACTION: Use Control Service-owned ordering independent of wall-clock, or require durable per-equipment prediction sequence/epoch with producer fencing.

DEC-004  
ROUND2_VERDICT: STILL_OPEN  
REASON: Claude says manual mode was deleted, but `API_AND_COMMAND_CONTRACTS.md` §Rules still allows “explicit human/manual mode” as a production command origin. `OPEN_DECISIONS.md` has no deferred manual-control entry.  
REMAINING_ACTION: Remove manual origin from baseline contracts or fully specify it.

DEC-005  
ROUND2_VERDICT: STILL_OPEN  
REASON: Claude says the gate list was unified, but `MASTER_SPEC.md` §7 still has 9 gates, `AI_SAFETY_AND_MLOPS.md` §Safety gate order still has 10, and `FAILURE_MODEL.md` still says OOD may “reject or reduce AI authority.”  
REMAINING_ACTION: Make one canonical gate table and make OOD a hard reject everywhere.

DEC-006  
ROUND2_VERDICT: STILL_OPEN  
REASON: The command proto still carries self-asserted `source` in `ApplyCommandRequest`; there is no server-derived `authenticatedSource` in the response/outcome contract and no local token/mTLS rotation contract.  
REMAINING_ACTION: Update proto/API/outcome contracts and security docs with authenticated origin and rotation semantics.

DEC-007  
ROUND2_VERDICT: STILL_OPEN  
REASON: `KAFKA_TOPOLOGY_AND_SEMANTICS.md` defines topic semantics, but `EVENT_CONTRACTS.md` still has non-envelope event examples and the machine-readable `contracts/` directory still only contains telemetry. Contract precedence remains theoretical without schemas for prediction/decision/outcome/state/model-deployment.  
REMAINING_ACTION: Create/update authoritative schemas and align `EVENT_CONTRACTS.md` with the minimal envelope.

DEC-008  
ROUND2_VERDICT: STILL_OPEN  
REASON: The prose in `TIME_AND_DATA_QUALITY.md` §5-§7 says closed flag enum and nullable values, but `contracts/jsonschema/v1/telemetry.schema.json` still has `flags.items.type=string` and all measurements as `number` only. The claimed contract change was not made.  
REMAINING_ACTION: Update telemetry schema and fixtures; add validation tests for nulls and closed quality flags.

DEC-009  
ROUND2_VERDICT: NEW_PROBLEM_INTRODUCED  
REASON: Moving authorization to `factory.model-deployments.v1` removes Supervisor-to-MLflow polling, but introduces `mlops-publisher` as an undocumented safety-critical propagation component. If quarantine occurs while `mlops-publisher` is down, Kafka remains reachable and quiet, so the Supervisor never trips the proposed Kafka staleness bound.  
REMAINING_ACTION: Define publisher availability/heartbeat/watermark semantics, quarantine write durability, startup policy, and ADR/NFR-012 rationale for the new component.

PART B — REVIEW OF THE THREE NEW DOCUMENTS

ID: CODEX-R2-001  
SEVERITY: P0  
FILE: docs/02-architecture/EQUIPMENT_MODEL_AND_STATE.md  
DEFECT: §3 says the state topic is `factory.equipment-state.v1`, but `KAFKA_TOPOLOGY_AND_SEMANTICS.md` §2 defines `factory.equipment-states.v1`.  
FIX: Use one topic name everywhere.

ID: CODEX-R2-002  
SEVERITY: P1  
FILE: docs/02-architecture/EQUIPMENT_MODEL_AND_STATE.md  
DEFECT: §1.4 references `docs/11-service-design/CONTROL_SERVICE.md`, but that file does not exist.  
FIX: Create the service design doc or remove the normative reference.

ID: CODEX-R2-003  
SEVERITY: P0  
FILE: docs/02-architecture/EQUIPMENT_MODEL_AND_STATE.md  
DEFECT: §4 says `operationRatePct` is required for the rate-of-change baseline, but Claude’s resolution says baseline is last accepted commanded setpoint, not applied `operationRatePct`.  
FIX: Separate tracking/confirmation use of `operationRatePct` from rate-budget baseline.

ID: CODEX-R2-004  
SEVERITY: P1  
FILE: docs/02-architecture/EQUIPMENT_MODEL_AND_STATE.md  
DEFECT: §3.3 moves equipment to `DEGRADED` for any advisory sensor null/flag, and §3.1 says `DEGRADED` is not AI eligible; this contradicts §4 saying advisory-channel incompleteness proceeds without hard reject.  
FIX: Decide whether advisory quality suspends AI or only marks prediction quality.

ID: CODEX-R2-005  
SEVERITY: P1  
FILE: docs/03-contracts/OT_PROTOCOL_MAPPING.md  
DEFECT: §2.3 names `sourceEpochMs` but describes it as “seconds since equipment start”; §2.6 relies on it for restart/freeze detection.  
FIX: Make the unit either milliseconds or seconds consistently and define resolution/wrap behavior.

ID: CODEX-R2-006  
SEVERITY: P0  
FILE: docs/03-contracts/OT_PROTOCOL_MAPPING.md  
DEFECT: §2.4 defines holding register offset `+0` for the single write but gives no per-equipment holding-register base, unlike §2.3 input registers. Multiple equipment would alias the same write address.  
FIX: Define holding-register base address per equipment.

ID: CODEX-R2-007  
SEVERITY: P1  
FILE: docs/03-contracts/OT_PROTOCOL_MAPPING.md  
DEFECT: §2.5 says “bit set = channel valid” but also reserves bit 15 as “simulator is injecting a fault,” which is not a validity bit.  
FIX: Split channel validity bitmap from status/fault bitmap.

ID: CODEX-R2-008  
SEVERITY: P0  
FILE: docs/03-contracts/KAFKA_TOPOLOGY_AND_SEMANTICS.md  
DEFECT: §5 omits Claude’s required Supervisor `seekToEnd` startup behavior, and §6 says restart resumes from committed offsets.  
FIX: Add Supervisor-specific seek/discard semantics and override the generic restart rule.

ID: CODEX-R2-009  
SEVERITY: P1  
FILE: docs/03-contracts/KAFKA_TOPOLOGY_AND_SEMANTICS.md  
DEFECT: §4 claims to close replication/config gaps but only defines RF; no `min.insync.replicas` is specified despite `acks=all`.  
FIX: Specify `min.insync.replicas` for local/prod-like profiles.

ID: CODEX-R2-010  
SEVERITY: P0  
FILE: docs/03-contracts/KAFKA_TOPOLOGY_AND_SEMANTICS.md  
DEFECT: §2 defines `factory.model-deployments.v1` with producer `mlops-publisher`, but no model-deployment schema, heartbeat, publisher failure semantics, or quarantine durability contract exists.  
FIX: Add the model authorization event contract and publisher failure/backfill requirements.

ID: CODEX-R2-011  
SEVERITY: P1  
FILE: docs/03-contracts/KAFKA_TOPOLOGY_AND_SEMANTICS.md  
DEFECT: §7 duplicate identity excludes `factory.model-deployments.v1` and `factory.faults.v1`, although both are in the topic register.  
FIX: Define duplicate identity for every registered topic.

ID: CODEX-R2-012  
SEVERITY: P0  
FILE: docs/03-contracts/KAFKA_TOPOLOGY_AND_SEMANTICS.md  
DEFECT: §12 says Supervisor lag 200 equals 50s at 5s cadence × 20 equipment, but a per-equipment keyed topic with 12 partitions can concentrate lag unevenly; the threshold does not guarantee per-equipment TTL safety.  
FIX: Define lag/age alerts per equipment or per assigned partition using record timestamp age.

PART C — CONVERGENCE STATEMENT

NOT_CONVERGED

Blocks convergence: DEC-001 epoch/lease persistence and RPC contract; DEC-002 Supervisor restart seek semantics; DEC-003 timestamp-based ordering; DEC-004 manual-origin removal; DEC-005 gate/OOD contradictions; DEC-006 command authenticity contract; DEC-007 missing authoritative schemas; DEC-008 telemetry schema mismatch; DEC-009 `mlops-publisher` quarantine propagation SPOF.

Codex session ID: 01a0a270-9f0a-7801-88ad-d99cc055c98e
Resume in Codex: codex resume 01a0a270-9f0a-7801-88ad-d99cc055c98e
