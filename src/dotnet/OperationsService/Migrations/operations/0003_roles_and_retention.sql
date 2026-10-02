-- Least privilege for the operations schema, and retention without row deletes (OD-023,
-- OPERATIONAL_DATA.md §15-§16). The control schema's roles are control-service's, in Phase 7 (OD-016).
--
-- Group roles, without login. A deployment grants them to its runtime login; each of the service's
-- data sources sets its own role on connect, so one connection cannot do what another may.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mair_ops_projector') THEN
        CREATE ROLE mair_ops_projector NOLOGIN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mair_ops_reader') THEN
        CREATE ROLE mair_ops_reader NOLOGIN;
    END IF;
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'mair_ops_retention') THEN
        CREATE ROLE mair_ops_retention NOLOGIN;
    END IF;
END
$$;

REVOKE ALL ON SCHEMA operations FROM PUBLIC;
REVOKE ALL ON ALL TABLES IN SCHEMA operations FROM PUBLIC;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA operations FROM PUBLIC;

GRANT USAGE ON SCHEMA operations TO mair_ops_projector, mair_ops_reader, mair_ops_retention;

-- The projector. Audit tables: INSERT only - append-only (SECURITY_BOUNDARIES.md), first-wins
-- (OD-023). SELECT is what ON CONFLICT needs to see the existing row; it changes nothing.
GRANT SELECT, INSERT ON safety_decision, control_outcome TO mair_ops_projector;
GRANT SELECT, INSERT, UPDATE, DELETE ON telemetry_reading_1s, prediction, equipment_state_history,
    equipment_decommission, model_deployment, authorization_watermark TO mair_ops_projector;

-- Partitions are created on demand before a write. The function runs as its owner, so the
-- projector needs no CREATE on the schema.
ALTER FUNCTION ensure_partition(text, timestamptz) SECURITY DEFINER SET search_path = operations, pg_temp;
GRANT EXECUTE ON FUNCTION ensure_partition(text, timestamptz) TO mair_ops_projector;

-- The API: reads, and nothing else.
GRANT SELECT ON ALL TABLES IN SCHEMA operations TO mair_ops_reader;
GRANT SELECT ON current_equipment_state TO mair_ops_reader;
REVOKE SELECT ON schema_migrations FROM mair_ops_reader;

-- Retention: whole partitions whose upper bound is past their table's period, for five
-- allow-listed tables. equipment_decommission is never on the list. Runs as the owner; the
-- retention role may call it and do nothing else - no row DELETE anywhere.
CREATE FUNCTION drop_expired_partitions(now_utc timestamptz) RETURNS SETOF text
LANGUAGE plpgsql SECURITY DEFINER SET search_path = operations, pg_temp AS $$
DECLARE
    keep  constant jsonb := '{"telemetry_reading_1s": "30 days", "prediction": "30 days",
                              "safety_decision": "90 days", "control_outcome": "1 year",
                              "equipment_state_history": "1 year"}';
    p     record;
    upper timestamptz;
BEGIN
    FOR p IN
        SELECT parent.relname AS parent, child.relname AS child, pg_get_expr(child.relpartbound, child.oid) AS bound
        FROM pg_inherits i
        JOIN pg_class parent ON parent.oid = i.inhparent
        JOIN pg_class child ON child.oid = i.inhrelid
        JOIN pg_namespace n ON n.oid = parent.relnamespace
        WHERE n.nspname = 'operations' AND keep ? parent.relname
        ORDER BY child.relname
    LOOP
        upper := substring(p.bound FROM 'TO \(''([^'']+)''\)')::timestamptz;
        IF upper <= now_utc - (keep ->> p.parent)::interval THEN
            EXECUTE format('DROP TABLE operations.%I', p.child);
            RETURN NEXT p.child;
        END IF;
    END LOOP;
END;
$$;

-- PostgreSQL grants EXECUTE on a new function to PUBLIC. The REVOKE above ran before this
-- function existed, so it is revoked here: without it any role could drop partitions (found by
-- AuditPrivilegeTests, 2026-10-02).
REVOKE ALL ON FUNCTION drop_expired_partitions(timestamptz) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION drop_expired_partitions(timestamptz) TO mair_ops_retention;
