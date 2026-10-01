-- The operations schema's projection tables (OPERATIONAL_DATA.md §7-§9a). Applied by
-- MigrationRunner inside one transaction with search_path set to the schema.
--
-- Rules every table here follows, because the canonical dump depends on them (OD-014):
--   * the primary key is the topic's duplicate identity (KAFKA_TOPOLOGY_AND_SEMANTICS.md §7),
--     plus the partition key where PostgreSQL requires a partitioned table's key to contain it;
--   * every time comes from the record; there is no sequence, no identity column, no DEFAULT
--     calling now(), and no insertion timestamp;
--   * every row carries its provenance - source_topic, source_partition, source_offset - so a
--     rebuild replaces exactly the rows of the range it replays and nothing else.
--
-- Time-series tables are range-partitioned: daily for the 30-day tables, monthly for the rest
-- (OD-018). Partitions are created on demand by ensure_partition() below, before a write.
--
-- Not here: fault_injection - factory.faults.v1 has no contract yet (OD-020).

-- factory.telemetry.v1 -> the latest reading per equipment per second (OD-015).
-- Kept: the reading with the greatest (event_time_utc, sequence) in its second.
CREATE TABLE telemetry_reading_1s (
    equipment_id        text             NOT NULL,
    second_utc          timestamptz      NOT NULL,
    event_time_utc      timestamptz      NOT NULL,
    sequence            bigint           NOT NULL,
    occurred_at_utc     timestamptz      NOT NULL,
    ingest_time_utc     timestamptz      NOT NULL,
    temperature_c       double precision,
    vibration_rms       double precision,
    current_a           double precision,
    voltage_v           double precision,
    rpm                 double precision,
    torque_nm           double precision,
    operation_rate_pct  double precision,
    quality_overall     text             NOT NULL,
    quality_flags       jsonb            NOT NULL,
    equipment_state     text,
    correlation_id      uuid             NOT NULL,
    source_topic        text             NOT NULL,
    source_partition    integer          NOT NULL,
    source_offset       bigint           NOT NULL,
    PRIMARY KEY (equipment_id, second_utc)
) PARTITION BY RANGE (second_utc);

-- factory.equipment-states.v1 -> every observed state record. Audit: kept for a year, and not
-- rebuildable, because the topic is compacted (OD-014).
CREATE TABLE equipment_state_history (
    equipment_id        text             NOT NULL,
    gateway_epoch       bigint           NOT NULL,
    state_sequence      bigint           NOT NULL,
    occurred_at_utc     timestamptz      NOT NULL,
    state               text             NOT NULL,
    previous_state      text,
    transition_id       text,
    ai_eligible         boolean          NOT NULL,
    observed_by         text             NOT NULL,
    correlation_id      uuid             NOT NULL,
    source_topic        text             NOT NULL,
    source_partition    integer          NOT NULL,
    source_offset       bigint           NOT NULL,
    PRIMARY KEY (equipment_id, gateway_epoch, state_sequence, occurred_at_utc)
) PARTITION BY RANGE (occurred_at_utc);

-- factory.predictions.v1. The chain starts here (OD-013): correlation_id is minted by the
-- prediction, causation_id is its feature window.
CREATE TABLE prediction (
    prediction_id       uuid             NOT NULL,
    occurred_at_utc     timestamptz      NOT NULL,
    equipment_id        text             NOT NULL,
    predicted_at_utc    timestamptz      NOT NULL,
    model_name          text             NOT NULL,
    model_version       text             NOT NULL,
    deployment_stage    text             NOT NULL,
    window_id           uuid             NOT NULL,
    window_start_utc    timestamptz      NOT NULL,
    window_end_utc      timestamptz      NOT NULL,
    anomaly_score       double precision NOT NULL,
    failure_probability double precision,
    rul_minutes         double precision,
    confidence          double precision NOT NULL,
    ood_score           double precision NOT NULL,
    correlation_id      uuid             NOT NULL,
    causation_id        uuid             NOT NULL,
    record              jsonb            NOT NULL,
    source_topic        text             NOT NULL,
    source_partition    integer          NOT NULL,
    source_offset       bigint           NOT NULL,
    PRIMARY KEY (prediction_id, occurred_at_utc)
) PARTITION BY RANGE (occurred_at_utc);

CREATE TABLE safety_decision (
    decision_id         uuid             NOT NULL,
    occurred_at_utc     timestamptz      NOT NULL,
    equipment_id        text             NOT NULL,
    prediction_id       uuid,
    decided_at_utc      timestamptz      NOT NULL,
    decision            text             NOT NULL,
    control_mode        text,
    reason_codes        jsonb            NOT NULL,
    correlation_id      uuid             NOT NULL,
    causation_id        uuid,
    record              jsonb            NOT NULL,
    source_topic        text             NOT NULL,
    source_partition    integer          NOT NULL,
    source_offset       bigint           NOT NULL,
    PRIMARY KEY (decision_id, occurred_at_utc)
) PARTITION BY RANGE (occurred_at_utc);

-- factory.control-outcomes.v1. A mode transition is an outcome carrying mode_transition_id and
-- from_mode (OD-019); those are the audited mode transitions of AC-029.
CREATE TABLE control_outcome (
    command_id          uuid             NOT NULL,
    occurred_at_utc     timestamptz      NOT NULL,
    equipment_id        text             NOT NULL,
    decision_id         uuid,
    status              text             NOT NULL,
    reason_code         text             NOT NULL,
    control_epoch       bigint           NOT NULL,
    resulting_mode      text             NOT NULL,
    from_mode           text,
    mode_transition_id  text,
    autonomous          boolean,
    correlation_id      uuid             NOT NULL,
    causation_id        uuid,
    record              jsonb            NOT NULL,
    source_topic        text             NOT NULL,
    source_partition    integer          NOT NULL,
    source_offset       bigint           NOT NULL,
    PRIMARY KEY (command_id, occurred_at_utc)
) PARTITION BY RANGE (occurred_at_utc);

-- factory.model-deployments.v1, AUTHORIZATION records. The topic is compacted: within retention
-- this holds the latest per model only (OD-014).
CREATE TABLE model_deployment (
    partition_key               text        NOT NULL,
    model_version               text        NOT NULL,
    authorization_sequence      bigint      NOT NULL,
    model_name                  text        NOT NULL,
    model_run_id                text        NOT NULL,
    deployment_stage            text        NOT NULL,
    quarantined                 boolean     NOT NULL,
    authorized_equipment_cohort jsonb,
    feature_schema_version      integer     NOT NULL,
    issued_at_utc               timestamptz,
    valid_until_utc             timestamptz,
    occurred_at_utc             timestamptz NOT NULL,
    correlation_id              uuid        NOT NULL,
    record                      jsonb       NOT NULL,
    source_topic                text        NOT NULL,
    source_partition            integer     NOT NULL,
    source_offset               bigint      NOT NULL,
    PRIMARY KEY (partition_key, model_version, authorization_sequence)
);

-- factory.model-deployments.v1, WATERMARK records: liveness, one row - the newest by sequence.
-- /models reports its age (ADR-0019).
CREATE TABLE authorization_watermark (
    partition_key           text        NOT NULL PRIMARY KEY,
    authorization_sequence  bigint      NOT NULL,
    occurred_at_utc         timestamptz NOT NULL,
    issued_at_utc           timestamptz,
    source_topic            text        NOT NULL,
    source_partition        integer     NOT NULL,
    source_offset           bigint      NOT NULL
);

-- The indexes the routes and the trace need (OPERATIONAL_DATA.md §8). The correlation_id indexes
-- are what make the trace one query rather than a scan (AC-047).
CREATE INDEX telemetry_reading_1s_second ON telemetry_reading_1s (second_utc);
CREATE INDEX equipment_state_history_equipment ON equipment_state_history (equipment_id, occurred_at_utc);
CREATE INDEX prediction_equipment ON prediction (equipment_id, occurred_at_utc);
CREATE INDEX prediction_correlation ON prediction (correlation_id);
CREATE INDEX safety_decision_equipment ON safety_decision (equipment_id, occurred_at_utc);
CREATE INDEX safety_decision_correlation ON safety_decision (correlation_id);
CREATE INDEX control_outcome_equipment ON control_outcome (equipment_id, occurred_at_utc);
CREATE INDEX control_outcome_correlation ON control_outcome (correlation_id);

-- Provenance, for range rebuilds (OD-014).
CREATE INDEX telemetry_reading_1s_source ON telemetry_reading_1s (source_topic, source_partition, source_offset);
CREATE INDEX equipment_state_history_source ON equipment_state_history (source_topic, source_partition, source_offset);
CREATE INDEX prediction_source ON prediction (source_topic, source_partition, source_offset);
CREATE INDEX safety_decision_source ON safety_decision (source_topic, source_partition, source_offset);
CREATE INDEX control_outcome_source ON control_outcome (source_topic, source_partition, source_offset);
CREATE INDEX model_deployment_source ON model_deployment (source_topic, source_partition, source_offset);
CREATE INDEX authorization_watermark_source ON authorization_watermark (source_topic, source_partition, source_offset);

-- Daily for the 30-day tables, monthly for the audit tables (OD-018). Named <table>_pYYYYMMDD or
-- <table>_pYYYYMM. Idempotent; the lock makes two concurrent callers safe.
CREATE FUNCTION ensure_partition(parent text, at_utc timestamptz) RETURNS text
LANGUAGE plpgsql AS $$
DECLARE
    daily  boolean := parent IN ('telemetry_reading_1s', 'prediction');
    lower  timestamptz;
    upper  timestamptz;
    name   text;
BEGIN
    IF parent NOT IN ('telemetry_reading_1s', 'prediction', 'equipment_state_history', 'safety_decision', 'control_outcome') THEN
        RAISE EXCEPTION 'ensure_partition: % is not a partitioned projection table', parent;
    END IF;

    IF daily THEN
        lower := date_trunc('day', at_utc, 'UTC');
        upper := lower + interval '1 day';
        name  := parent || '_p' || to_char(lower AT TIME ZONE 'UTC', 'YYYYMMDD');
    ELSE
        lower := date_trunc('month', at_utc, 'UTC');
        upper := (lower AT TIME ZONE 'UTC' + interval '1 month') AT TIME ZONE 'UTC';
        name  := parent || '_p' || to_char(lower AT TIME ZONE 'UTC', 'YYYYMM');
    END IF;

    PERFORM pg_advisory_xact_lock(hashtext('operations.ensure_partition'));
    IF to_regclass(format('operations.%I', name)) IS NULL THEN
        EXECUTE format('CREATE TABLE operations.%I PARTITION OF operations.%I FOR VALUES FROM (%L) TO (%L)',
                       name, parent, lower, upper);
    END IF;
    RETURN name;
END;
$$;
