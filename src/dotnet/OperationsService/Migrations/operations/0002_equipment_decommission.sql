-- Tombstones on factory.equipment-states.v1 (OD-021, OPERATIONAL_DATA.md §10).
--
-- One row per tombstone record. A tombstone carries no payload, so the record is its identity:
-- the primary key is its provenance, and a redelivery lands on the same row
-- (KAFKA_TOPOLOGY_AND_SEMANTICS.md §7). decommissioned_at_utc is the record's Kafka CreateTime,
-- set by the gateway and stored in the log, so a replay reads back the same value.
--
-- Never deleted, and not partitioned: it is an input to the current view as well as audit, and
-- it is bounded by the inventory.
CREATE TABLE equipment_decommission (
    equipment_id           text        NOT NULL,
    decommissioned_at_utc  timestamptz NOT NULL,
    source_topic           text        NOT NULL,
    source_partition       integer     NOT NULL,
    source_offset          bigint      NOT NULL,
    PRIMARY KEY (source_topic, source_partition, source_offset)
);

-- The current view compares an equipment's newest state record with its newest tombstone by
-- offset; both share the equipment's key and so its partition.
CREATE INDEX equipment_decommission_equipment ON equipment_decommission (equipment_id, source_offset);

-- The current view itself (OD-021): an equipment is current when its newest state record is at a
-- later offset than its newest tombstone. An equipment that reappears is current again.
CREATE VIEW current_equipment_state AS
SELECT s.*
FROM (
    SELECT DISTINCT ON (equipment_id) *
    FROM equipment_state_history
    ORDER BY equipment_id, source_offset DESC
) s
WHERE NOT EXISTS (
    SELECT 1 FROM equipment_decommission d
    WHERE d.equipment_id = s.equipment_id
      AND d.source_partition = s.source_partition
      AND d.source_offset > s.source_offset
);
