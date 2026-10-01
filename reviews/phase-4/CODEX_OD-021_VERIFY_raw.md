# Codex verification of OD-021 as applied

> Raw output, verbatim, `codex exec -s read-only`, 2026-10-01.

# Round 1 - verdict REVISE

Read-only verification complete. I did not modify files.

**Condition Check**
1. **NOT MET**: The docs say tombstone allowance is recorded in `TopicSpec`, but the normative topic register table has no “allows tombstones” column, and Phase 3 `TopicSpec` still has only name/partitions/cleanup/retention.
2. **MET**: Disallowed nulls are specified as `Unparseable` first-attempt DLQ; allowed tombstones go to a separate tombstone handler with handler-failure retry/DLQ semantics.
3. **NOT MET**: The projection behavior is described, but the table shape is not actually specified enough to implement: no column list/PK for `equipment_decommission`, and §7 duplicate identity still omits tombstones.
4. **MET**: `SAFETY_SUPERVISOR.md` now says the Supervisor uses the shared consumer semantics and removes equipment from current-state view on tombstone.
5. **MET**: Replay/DLQ handling is specified for tombstones in offset order, with commit after settle and DLQ on handler failure.
6. **NOT MET**: `KAFKA-003` and `AUDIT-001` cover most cases, but `AUDIT-001` does not explicitly assert replay determinism for tombstone projection despite OD-021 claiming it.

**Findings**
P4-T-001 [P1]  
File: [KAFKA_TOPOLOGY_AND_SEMANTICS.md](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/docs/03-contracts/KAFKA_TOPOLOGY_AND_SEMANTICS.md:154)  
Defect: The prose says tombstone allowance is “recorded as allows tombstones in the topic register,” but the register table at lines 19-27 has no such column. That leaves the authoritative contract ambiguous and prevents `AC-026`/bootstrap-style drift checks from ever proving the metadata.  
Fix: Add an explicit `Allows tombstones` column to the topic register, with `yes` only for `factory.equipment-states.v1`; mirror it in `TopicSpec` and tests.

P4-T-002 [P1]  
File: [KAFKA_TOPOLOGY_AND_SEMANTICS.md](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/docs/03-contracts/KAFKA_TOPOLOGY_AND_SEMANTICS.md:171)  
Defect: §7 still defines `factory.equipment-states.v1` duplicate identity only as `(equipmentId, gatewayEpoch, stateSequence)`. Tombstones have none of those fields, while `OPERATIONAL_DATA.md` assigns them provenance identity. That is a contract conflict.  
Fix: Amend the duplicate identity row to distinguish state records from tombstones, e.g. state payloads use `(equipmentId, gatewayEpoch, stateSequence)`; tombstones use `(sourceTopic, sourcePartition, sourceOffset)`.

P4-T-003 [P1]  
File: [OPERATIONAL_DATA.md](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/docs/11-service-design/OPERATIONAL_DATA.md:86)  
Defect: `equipment_decommission` is named and behavior is described, but the projection shape is not fully defined before code: no required columns, primary key, indexes, retention/partitioning choice, or canonical dump order. The existing migration has no table for it.  
Fix: Specify the table shape explicitly, including at least `equipment_id`, `decommissioned_at_utc`, `source_topic`, `source_partition`, `source_offset`, and the PK; then align the migration/tests.

P4-T-004 [P2]  
File: [OPERATIONAL_DATA.md](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/docs/11-service-design/OPERATIONAL_DATA.md:88)  
Defect: Kafka record timestamp is deterministic when replaying the same retained log record, but the spec does not pin whether the timestamp is producer `CreateTime` or broker `LogAppendTime`. If `decommissioned_at_utc` is audit semantics, that ambiguity matters.  
Fix: State the accepted timestamp type and why it is the decommission audit time, or store it as `observed_record_timestamp_utc` and avoid implying business-time precision.

P4-T-005 [P2]  
File: [TEST_SPECIFICATIONS.md](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/docs/06-development/TEST_SPECIFICATIONS.md:402)  
Defect: `AUDIT-001` checks redelivery idempotency, but not the claimed replay determinism for tombstone projection/current view. Redelivery of the same offset is weaker than replaying the retained range into a fresh store and comparing canonical dumps.  
Fix: Add an assertion that a retained range containing state, tombstone, and later state rebuilds `equipment_decommission` and the current view byte-identically.

**Design Challenge**
Kafka offset comparison is sound only within the same partition; here the key is `equipmentId`, so state and tombstone offsets are comparable for that equipment. Gateway restarts and new `gatewayEpoch` do not break that, because offset order is independent of payload duplicate identity. Compaction also does not break the operations DB rule once the rows are already projected, but after tombstone expiry the Kafka log alone is not a complete audit source, which the docs correctly mark as not rebuildable.

Keying tombstones by provenance is sound for delivery duplicates of the same Kafka record. It is not a semantic duplicate identity for repeated tombstones emitted at different offsets, so the docs should be explicit that “one row” means one row per tombstone record, not necessarily one row per equipment decommission episode, unless additional coalescing rules are added.

Verdict: **REVISE**.

# Round 2 - verdict REVISE

Read-only verification complete. I did not modify files.

**P4-T Status**
- P4-T-001: CLOSED
- P4-T-002: CLOSED
- P4-T-003: CLOSED for table shape; see new P1 below
- P4-T-004: CLOSED
- P4-T-005: CLOSED

**OD-021 Conditions**
1. MET
2. MET
3. NOT MET
4. MET
5. MET
6. MET

**New P0/P1 Findings**
P4-T-006 [P1]  
File: [OPERATIONAL_DATA.md](C:/Users/user/Desktop/workspace/Portfolio/manufacturing-ai-reliability/docs/11-service-design/OPERATIONAL_DATA.md:98)  
Defect: `equipment_decommission` is both the only projected record of a tombstone after Kafka compaction and part of the current-view rule, but the new text gives it 1 y retention and deletes rows past it. After that retention job runs, an equipment with an older state record and a deleted tombstone can be incorrectly treated as current again. This contradicts OD-021’s “current equipment excludes it” rule and the table’s own “audit / not rebuildable” classification.  
Fix: Make `equipment_decommission` non-truncated like the other non-rebuildable audit/current-state inputs, or define a separate indefinite current-state marker so retention cannot resurrect decommissioned equipment.

Verdict: **REVISE**.

# Round 3 - verdict ACCEPT

P4-T-006 is CLOSED. `equipment_decommission` is now specified as not partitioned, never deleted, and retained indefinitely as an input to the current view.

OD-021 condition 3 is MET. The projection shape now defines the table, key/provenance identity, timestamp source, replay-stable semantics, and current-view offset rule.

New P0/P1 findings: none.

Verdict: ACCEPT
