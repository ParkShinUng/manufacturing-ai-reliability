# Codex challenge of OD-020, OD-021 and the step 2 schema

> Raw output, transcribed verbatim from the `codex exec -s read-only` run of 2026-10-01. OD-020
> SOUND (A); OD-021 SOUND_WITH_CONDITIONS (A); one P3 on the schema.

Read-only challenge complete. I did not modify files.

**OD-020**
Verdict: **SOUND**  
Recommended option: **A**

Conditions:
1. Keep `fault_injection` absent from Phase 4 migrations and projector subscriptions until `factory.faults.v1` has a JSON schema and example.
2. `OPERATIONAL_DATA.md` must continue to mark the table as deferred, matching [0001_projection_tables.sql:15](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/src/dotnet/OperationsService/Migrations/operations/0001_projection_tables.sql:15).
3. Phase 11 must add schema, example, producer, projection, duplicate-identity tests, and consume-validate-DLQ tests together.

Reasoning:
`contracts/jsonschema/v1` has no faults schema.  
Topology still defines `factory.faults.v1` and `faultInjectionId` at [KAFKA_TOPOLOGY_AND_SEMANTICS.md:27](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/docs/03-contracts/KAFKA_TOPOLOGY_AND_SEMANTICS.md:27) and [KAFKA_TOPOLOGY_AND_SEMANTICS.md:169](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/docs/03-contracts/KAFKA_TOPOLOGY_AND_SEMANTICS.md:169).  
OD-009 requires later consumers to validate against authoritative schemas, so projecting an unschematized topic would invent a contract.  
Claude’s A matches the current migration and tests.

**OD-021**
Verdict: **SOUND_WITH_CONDITIONS**  
Recommended option: **A**

Conditions:
1. Tombstone allowance must be contract/topic metadata consumed by `ContractConsumer`, not a per-consumer filter or hidden topic-name special case.
2. The shared path needs a separate tombstone handler; null on topics without tombstone allowance remains invalid and goes to DLQ with required headers.
3. Operations must define the projection shape before code: history is retained, while a current-equipment view excludes/decommissions the key. Current `equipment_state_history` cannot record a tombstone as-is because `state`, `gateway_epoch`, `state_sequence`, and `correlation_id` are non-null at [0001_projection_tables.sql:45](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/src/dotnet/OperationsService/Migrations/operations/0001_projection_tables.sql:45).
4. Phase 6 Safety Supervisor must use the same tombstone semantics to remove equipment from its current-state view; no second interpretation.
5. Replay and DLQ behavior must be specified: allowed tombstones are processed in offset order and committed; disallowed/null handler failures preserve source topic/partition/offset headers.
6. Add tests for allowed tombstone, disallowed tombstone, handler failure, projector current/history behavior, replay determinism, and later Supervisor adoption.

Reasoning:
The topology says equipment-state is compacted and tombstones are legitimate at [KAFKA_TOPOLOGY_AND_SEMANTICS.md:139](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/docs/03-contracts/KAFKA_TOPOLOGY_AND_SEMANTICS.md:139) and [KAFKA_TOPOLOGY_AND_SEMANTICS.md:149](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/docs/03-contracts/KAFKA_TOPOLOGY_AND_SEMANTICS.md:149).  
The gateway implements null tombstones at [KafkaEquipmentStateSink.cs:96](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/src/dotnet/EdgeGateway/KafkaEquipmentStateSink.cs:96).  
`ContractConsumer` validates every value at [ContractConsumer.cs:154](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/src/dotnet/EventBackbone/ContractConsumer.cs:154), and invalid bytes are DLQ’d.  
Option B duplicates the rule despite OD-009’s shared-path condition at [OPEN_DECISIONS.md:577](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/docs/08-roadmap/OPEN_DECISIONS.md:577).  
Option C reverses OD-008.

**Other Finding**
P4-S2-001 [P3]  
File: [0001_projection_tables.sql:176](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/src/dotnet/OperationsService/Migrations/operations/0001_projection_tables.sql:176)  
Defect: the migration says provenance indexes support range rebuilds, but creates source indexes only through `control_outcome`; `model_deployment` and `authorization_watermark` also carry provenance columns at lines 147-149 and 160-162.  
Fix: add source indexes for both compacted model-deployment tables and a schema test that every projection table with `source_topic/source_partition/source_offset` has the provenance index.
