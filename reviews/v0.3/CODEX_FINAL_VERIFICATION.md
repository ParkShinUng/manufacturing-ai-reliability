PART A - ROUND-2 FINDING CLOSURE

DEC-001 | CLOSED | `control_epoch`, `AcquireControlLease`, `Heartbeat`, lease expiry exist in `contracts/proto/control/v1/control.proto:23-63,77-87,105-140`; persistence-before-write and restart increment are in `docs/11-service-design/CONTROL_SERVICE.md:81-89`.

DEC-002 | CLOSED | Supervisor-specific `seekToEnd` override is now explicit in `docs/03-contracts/KAFKA_TOPOLOGY_AND_SEMANTICS.md:106-124`, with lease interaction in `:132-141`.

DEC-003 | NOT_CLOSED | The “no timestamp ordering” decision is contradicted by current contracts: `prediction.schema.json:34-38`, `safety-decision.schema.json:34-37`, and `CONTROL_SERVICE.md:108-113` still use `sourcePredictionAtUtc` for supersession; `CONTROL_SERVICE.md:112` also appears inverted.

DEC-004 | CLOSED | Manual origin is removed as a production path in `docs/03-contracts/API_AND_COMMAND_CONTRACTS.md:34`; deferred in `docs/08-roadmap/OPEN_DECISIONS.md:14-22`; absent from proto mode enum in `control.proto:164-171`.

DEC-005 | CLOSED | Single normative gate table is in `docs/04-ai/AI_SAFETY_AND_MLOPS.md:24-46`; `MASTER_SPEC.md:121-132` references it instead of duplicating; OOD hard reject is in `AI_SAFETY_AND_MLOPS.md:48-55`.

DEC-006 | PARTIAL | Proto/outcome are fixed (`control.proto:122-136`, `control-outcome.schema.json:17-20,98-101`), but stale prose example still contains `"source"` in `docs/03-contracts/API_AND_COMMAND_CONTRACTS.md:5-16` while line `35` says it was removed.

DEC-007 | CLOSED | Machine contracts outrank prose in `MASTER_SPEC.md:144-159`; schemas exist in `contracts/jsonschema/v1/`; examples are mapped in `contracts/examples/_manifest.json:1-10` and tests pass.

DEC-008 | CLOSED | Telemetry schema now has nullable measurements and closed quality flags in `contracts/jsonschema/v1/telemetry.schema.json:68-146`; derivation rules are in `TIME_AND_DATA_QUALITY.md:89-146`.

DEC-009 | PARTIAL | Watermark semantics exist in `DEC-009:50-56`, `model-authorization.schema.json:30-35`, and `MODEL_LIFECYCLE.md:61-69`; still blocked by required human approval for the 60s backstop in `DEC-009:79-92` and `safety-config.schema.json:137-140`.

CODEX-R2-001 | CLOSED | Topic is plural in `docs/02-architecture/EQUIPMENT_MODEL_AND_STATE.md:127`.

CODEX-R2-002 | CLOSED | `docs/11-service-design/CONTROL_SERVICE.md` exists and is referenced from `EQUIPMENT_MODEL_AND_STATE.md:86-88`.

CODEX-R2-003 | CLOSED | `operationRatePct` is now tracking confirmation, not baseline, in `EQUIPMENT_MODEL_AND_STATE.md:244`.

CODEX-R2-004 | CLOSED | Advisory sensor issue is resolved as `UNCERTAIN`/`DEGRADED`/AI suspended, not hard reject, in `EQUIPMENT_MODEL_AND_STATE.md:245-251`.

CODEX-R2-005 | CLOSED | `sourceEpochMs` is now milliseconds with wrap behavior in `OT_PROTOCOL_MAPPING.md:132`.

CODEX-R2-006 | CLOSED | Holding-register base per equipment is defined in `OT_PROTOCOL_MAPPING.md:134-137`.

CODEX-R2-007 | PARTIAL | Status bitmap was split out in `OT_PROTOCOL_MAPPING.md:164-166`, but line `169` still says “Bit 15 exists” in the quality bitmap section, contradicting the new split.

CODEX-R2-008 | CLOSED | `seekToEnd` override is explicit in `KAFKA_TOPOLOGY_AND_SEMANTICS.md:106-124`.

CODEX-R2-009 | CLOSED | `min.insync.replicas` is specified in `KAFKA_TOPOLOGY_AND_SEMANTICS.md:114`.

CODEX-R2-010 | PARTIAL | Model authorization schema/watermark exist (`model-authorization.schema.json:1-35`), but compacted topic keying is still underspecified for watermark records because topic key is `modelName` in `KAFKA_TOPOLOGY_AND_SEMANTICS.md:26` while watermark example has no `modelName` in `contracts/examples/model-authorization.watermark.json:1-12`.

CODEX-R2-011 | CLOSED | Duplicate identities now include model deployments and faults in `KAFKA_TOPOLOGY_AND_SEMANTICS.md:146-158`.

CODEX-R2-012 | CLOSED | Record-age alerting per assigned partition is specified in `KAFKA_TOPOLOGY_AND_SEMANTICS.md:250-255`.

PART B - NEW FINDINGS

CODEX-R3-001 | P0 | `contracts/jsonschema/v1/prediction.schema.json:34-38`, `contracts/jsonschema/v1/safety-decision.schema.json:34-37`, `docs/11-service-design/CONTROL_SERVICE.md:108-113` | DEC-003 is not actually implemented: timestamp supersession remains, and `sourcePredictionAtUtc > lastAccepted? -> COMMAND_SUPERSEDED` appears backwards. | Remove timestamp ordering from contracts or fully specify it; fix the supersession predicate.

CODEX-R3-002 | P0 | `MASTER_SPEC.md:168-180`, missing `docs/10-human-review/v0.3/HUMAN_APPROVAL.md`, `DEC-001:86-88`, `DEC-004:76-79`, `DEC-009:90-92` | Repo claims gate closed but implementation is explicitly blocked pending human approval and three human-review-required decisions remain. | Do not call this implementation-ready until approval exists and HD-001/002/003 are resolved or explicitly deferred from implementation.

CODEX-R3-003 | P1 | `docs/03-contracts/API_AND_COMMAND_CONTRACTS.md:5-16,35` | Command contract prose contradicts itself: request example still includes self-asserted `source` while rule says field was removed. | Replace prose example with proto-shaped request including `controlEpoch`, `issuedAt`, `expiresAt`, no `source`.

CODEX-R3-004 | P1 | `KAFKA_TOPOLOGY_AND_SEMANTICS.md:26`, `model-authorization.schema.json:37-42`, `model-authorization.watermark.json:1-12` | Watermark on compacted topic has no contracted key; `modelName` is nullable/missing for watermark records. | Define a sentinel key such as `__watermark__`, require it for WATERMARK, and update duplicate identity.

CODEX-R3-005 | P1 | `docs/03-contracts/OT_PROTOCOL_MAPPING.md:132,164-170,184-185` | Modbus register map aliases `sourceEpochMs` at `+20` for 2 registers with `statusBitmap` at `+21`; quality section also still mentions bit 15. | Move `statusBitmap` to a non-overlapping register and update block length/table.

CODEX-R3-006 | P1 | `contracts/jsonschema/v1/safety-decision.schema.json:50-74`, `docs/04-ai/AI_SAFETY_AND_MLOPS.md:30-44` | Safety-decision schema does not enforce exactly 13 gates or canonical order; enum order even lists `SUPERVISOR_HEALTHY` last. | Use `prefixItems`/`minItems=13`/`maxItems=13` or a custom contract test for exact ordered gate list.

CODEX-R3-007 | P1 | `safety-config.schema.json:5,127-140`, `SAFETY_CONFIGURATION.md:71-87` | Safety config schema requires runtime-critical values but punts SC-01..SC-10 to prose/custom validation; current example test cannot catch unsafe cross-field configs. | Add executable invariant tests or encode what JSON Schema can express.

PART C - THE 19 QUESTIONS

1. No. Phase 1 HIL still needs invention/fix for Modbus register collision and stale command prose.
2. Yes. Service docs define responsibilities/non-responsibilities across all 12 designs.
3. Mostly yes. Control Service ownership is explicit, but DEC-003 supersession text is contradictory.
4. No direct bypass found. AI has no equipment credentials and Control Service is sole application writer.
5. Not confidently. Duplicate/stale/replay are mostly handled, but timestamp supersession contradiction blocks a clean yes.
6. Yes, sufficiently for v0.3, with failure matrix and service failure behavior.
7. Mostly yes. Retry/idempotency are defined, but safety-config invariant validation is not executable enough.
8. Mostly, but not fully: command prose, gateResults schema, watermark keying, and Modbus map are inconsistent.
9. Mostly. Control/equipment state machines are deterministic; command supersession predicate is not.
10. Yes. Timestamp producers and clock model are explicit.
11. Yes. Freshness/TTL/stale rules are defined.
12. Mostly yes. PostgreSQL in Control Service is correctly control-critical; Kafka/AI/UI are mostly isolated.
13. No obvious unnecessary major technology, but `mlops-publisher` is a new safety-relevant component and still needs final risk approval.
14. Mostly yes. ACs are testable, but some required tests are only specified, not executable yet.
15. Mostly no. Numbers are labelled targets/unmeasured, but “milliseconds” quarantine propagation is still asserted loosely.
16. They would challenge fallback rate validity, Modbus map collision, dead-man realism, and “simulator stop” wording.
17. They would challenge timestamp supersession, lease/epoch atomicity under DB outage, and watermark/backstop semantics.
18. They would challenge local token identity, credential rotation during degraded modes, and proof that no non-Control write credentials exist.
19. They would challenge OOD threshold governance, feature-schema drift, authorization watermark SPOF, and rollback propagation claims.

PART D - VERDICT

NOT_IMPLEMENTATION_READY | unresolved P0: 2 | unresolved P1: 5

The old round-2 fixes are mostly real, and the repo is much closer, but v0.3 is not implementation-ready. The blockers are concrete: DEC-003 still leaks timestamp-based supersession into contracts and Control Service design; implementation is explicitly gated on missing human approval and unresolved HD decisions; and several P1 contract defects remain in command prose, watermark keying, Modbus mapping, exact gate-result validation, and safety-config invariant enforcement.

Codex session ID: 01a0a28e-e5b9-7953-9478-0918288d9ce5
Resume in Codex: codex resume 01a0a28e-e5b9-7953-9478-0918288d9ce5
