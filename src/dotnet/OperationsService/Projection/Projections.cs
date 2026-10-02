using System.Globalization;
using System.Text;
using System.Text.Json;
using Confluent.Kafka;
using Npgsql;
using NpgsqlTypes;

namespace Mair.OperationsService.Projection;

/// <summary>
/// One write per topic into the <c>operations</c> schema (<c>OPERATIONAL_DATA.md</c> §7–§10). Every
/// record reaching here has already passed its contract schema in the shared consumer (<c>OD-009</c>),
/// so a missing required field is a bug, not data, and throws.
/// <para>
/// <b>Deterministic by construction</b> (<c>OD-014</c>): every value written comes from the record or
/// its offset — nothing from the clock, nothing generated. Every write is an upsert on the topic's
/// duplicate identity, so a redelivery or a replay writes the same row again.
/// </para>
/// </summary>
public sealed class Projections(NpgsqlDataSource db)
{
    public const string Schema = "operations";

    /// <summary>
    /// <c>telemetry_reading_1s</c> (OD-015): the reading with the greatest <c>(eventTimeUtc, sequence)</c>
    /// in its second. The <c>WHERE</c> on the conflict makes the result a function of which records
    /// exist, not of the order they arrive in.
    /// </summary>
    public async Task TelemetryAsync(ConsumeResult<string, byte[]> record, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(record.Message.Value);
        var r = doc.RootElement;
        var m = r.GetProperty("measurements"u8);
        var q = r.GetProperty("quality"u8);
        var eventTime = Time(r, "eventTimeUtc"u8);
        var second = new DateTime(eventTime.Ticks - (eventTime.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);

        await WriteAsync("telemetry_reading_1s", second, """
            INSERT INTO operations.telemetry_reading_1s VALUES
                ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20)
            ON CONFLICT (equipment_id, second_utc) DO UPDATE SET
                event_time_utc = EXCLUDED.event_time_utc, sequence = EXCLUDED.sequence,
                occurred_at_utc = EXCLUDED.occurred_at_utc, ingest_time_utc = EXCLUDED.ingest_time_utc,
                temperature_c = EXCLUDED.temperature_c, vibration_rms = EXCLUDED.vibration_rms,
                current_a = EXCLUDED.current_a, voltage_v = EXCLUDED.voltage_v, rpm = EXCLUDED.rpm,
                torque_nm = EXCLUDED.torque_nm, operation_rate_pct = EXCLUDED.operation_rate_pct,
                quality_overall = EXCLUDED.quality_overall, quality_flags = EXCLUDED.quality_flags,
                equipment_state = EXCLUDED.equipment_state, correlation_id = EXCLUDED.correlation_id,
                source_topic = EXCLUDED.source_topic, source_partition = EXCLUDED.source_partition,
                source_offset = EXCLUDED.source_offset
            WHERE (EXCLUDED.event_time_utc, EXCLUDED.sequence) > (telemetry_reading_1s.event_time_utc, telemetry_reading_1s.sequence)
            """,
            cancellationToken,
            Str(r, "equipmentId"u8), second, eventTime, r.GetProperty("sequence"u8).GetInt64(),
            Time(r, "occurredAtUtc"u8), Time(r, "ingestTimeUtc"u8),
            Num(m, "temperatureC"u8), Num(m, "vibrationRms"u8), Num(m, "currentA"u8), Num(m, "voltageV"u8),
            Num(m, "rpm"u8), Num(m, "torqueNm"u8), Num(m, "operationRatePct"u8),
            Str(q, "overall"u8), Json(q.GetProperty("flags"u8)), OptStr(r, "equipmentState"u8),
            Guid.Parse(Str(r, "correlationId"u8)),
            record.Topic, record.Partition.Value, record.Offset.Value);
    }

    /// <summary><c>equipment_state_history</c>: every state record, identity <c>(equipmentId, gatewayEpoch, stateSequence)</c>.</summary>
    public async Task EquipmentStateAsync(ConsumeResult<string, byte[]> record, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(record.Message.Value);
        var r = doc.RootElement;
        var occurred = Time(r, "occurredAtUtc"u8);

        await WriteAsync("equipment_state_history", occurred, """
            INSERT INTO operations.equipment_state_history VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13)
            ON CONFLICT (equipment_id, gateway_epoch, state_sequence, occurred_at_utc) DO UPDATE SET
                state = EXCLUDED.state, previous_state = EXCLUDED.previous_state, transition_id = EXCLUDED.transition_id,
                ai_eligible = EXCLUDED.ai_eligible, observed_by = EXCLUDED.observed_by, correlation_id = EXCLUDED.correlation_id,
                source_topic = EXCLUDED.source_topic, source_partition = EXCLUDED.source_partition, source_offset = EXCLUDED.source_offset
            """,
            cancellationToken,
            Str(r, "equipmentId"u8), r.GetProperty("gatewayEpoch"u8).GetInt64(), r.GetProperty("stateSequence"u8).GetInt64(),
            occurred, Str(r, "state"u8), OptStr(r, "previousState"u8), OptStr(r, "transitionId"u8),
            r.GetProperty("aiEligible"u8).GetBoolean(), Str(r, "observedBy"u8), Guid.Parse(Str(r, "correlationId"u8)),
            record.Topic, record.Partition.Value, record.Offset.Value);
    }

    /// <summary>
    /// A tombstone on <c>factory.equipment-states.v1</c> (OD-021): one row per tombstone record,
    /// keyed by the record itself; the time is the record's <c>CreateTime</c>, stored in the log.
    /// </summary>
    public async Task DecommissionAsync(ConsumeResult<string, byte[]> record, CancellationToken cancellationToken)
    {
        if (record.Message.Timestamp.Type != TimestampType.CreateTime)
        {
            // OD-021 pins CreateTime: the gateway's decision time. Anything else is not that fact.
            throw new InvalidOperationException($"tombstone at {record.TopicPartitionOffset} has a {record.Message.Timestamp.Type} timestamp, not CreateTime");
        }

        await using var cmd = db.CreateCommand("""
            INSERT INTO operations.equipment_decommission VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (source_topic, source_partition, source_offset) DO NOTHING
            """);
        Add(cmd, record.Message.Key, record.Message.Timestamp.UtcDateTime, record.Topic, record.Partition.Value, record.Offset.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task PredictionAsync(ConsumeResult<string, byte[]> record, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(record.Message.Value);
        var r = doc.RootElement;
        var model = r.GetProperty("model"u8);
        var window = r.GetProperty("featureWindow"u8);
        var outputs = r.GetProperty("outputs"u8);
        var occurred = Time(r, "occurredAtUtc"u8);

        await WriteAsync("prediction", occurred, """
            INSERT INTO operations.prediction VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17, $18, $19, $20, $21)
            ON CONFLICT (prediction_id, occurred_at_utc) DO UPDATE SET
                equipment_id = EXCLUDED.equipment_id, predicted_at_utc = EXCLUDED.predicted_at_utc,
                model_name = EXCLUDED.model_name, model_version = EXCLUDED.model_version, deployment_stage = EXCLUDED.deployment_stage,
                window_id = EXCLUDED.window_id, window_start_utc = EXCLUDED.window_start_utc, window_end_utc = EXCLUDED.window_end_utc,
                anomaly_score = EXCLUDED.anomaly_score, failure_probability = EXCLUDED.failure_probability,
                rul_minutes = EXCLUDED.rul_minutes, confidence = EXCLUDED.confidence, ood_score = EXCLUDED.ood_score,
                correlation_id = EXCLUDED.correlation_id, causation_id = EXCLUDED.causation_id, record = EXCLUDED.record,
                source_topic = EXCLUDED.source_topic, source_partition = EXCLUDED.source_partition, source_offset = EXCLUDED.source_offset
            """,
            cancellationToken,
            Guid.Parse(Str(r, "predictionId"u8)), occurred, Str(r, "equipmentId"u8), Time(r, "predictedAtUtc"u8),
            Str(model, "name"u8), Str(model, "version"u8), Str(model, "deploymentStage"u8),
            Guid.Parse(Str(window, "windowId"u8)), Time(window, "startUtc"u8), Time(window, "endUtc"u8),
            outputs.GetProperty("anomalyScore"u8).GetDouble(), Num(outputs, "failureProbability"u8), Num(outputs, "rulMinutes"u8),
            outputs.GetProperty("confidence"u8).GetDouble(), outputs.GetProperty("oodScore"u8).GetDouble(),
            Guid.Parse(Str(r, "correlationId"u8)), Guid.Parse(Str(r, "causationId"u8)), Record(record),
            record.Topic, record.Partition.Value, record.Offset.Value);
    }

    /// <summary>
    /// An audit record: insert-only (SECURITY_BOUNDARIES.md), first-wins in offset order (OD-023). A
    /// later record with the same identity - even a different payload - leaves the first row as it is.
    /// </summary>
    public async Task SafetyDecisionAsync(ConsumeResult<string, byte[]> record, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(record.Message.Value);
        var r = doc.RootElement;
        var occurred = Time(r, "occurredAtUtc"u8);

        await WriteAsync("safety_decision", occurred, """
            INSERT INTO operations.safety_decision VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)
            ON CONFLICT (decision_id, occurred_at_utc) DO NOTHING
            """,
            cancellationToken,
            Guid.Parse(Str(r, "decisionId"u8)), occurred, Str(r, "equipmentId"u8), OptGuid(r, "predictionId"u8),
            Time(r, "decidedAtUtc"u8), Str(r, "decision"u8), OptStr(r, "controlMode"u8), Json(r.GetProperty("reasonCodes"u8)),
            Guid.Parse(Str(r, "correlationId"u8)), OptGuid(r, "causationId"u8), Record(record),
            record.Topic, record.Partition.Value, record.Offset.Value);
    }

    /// <summary>An audit record, as <see cref="SafetyDecisionAsync"/>: insert-only, first-wins.</summary>
    public async Task ControlOutcomeAsync(ConsumeResult<string, byte[]> record, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(record.Message.Value);
        var r = doc.RootElement;
        var occurred = Time(r, "occurredAtUtc"u8);

        await WriteAsync("control_outcome", occurred, """
            INSERT INTO operations.control_outcome VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17)
            ON CONFLICT (command_id, occurred_at_utc) DO NOTHING
            """,
            cancellationToken,
            Guid.Parse(Str(r, "commandId"u8)), occurred, Str(r, "equipmentId"u8), OptGuid(r, "decisionId"u8),
            Str(r, "status"u8), Str(r, "reasonCode"u8), r.GetProperty("controlEpoch"u8).GetInt64(), Str(r, "resultingMode"u8),
            OptStr(r, "fromMode"u8), OptStr(r, "modeTransitionId"u8), OptBool(r, "autonomous"u8),
            Guid.Parse(Str(r, "correlationId"u8)), OptGuid(r, "causationId"u8), Record(record),
            record.Topic, record.Partition.Value, record.Offset.Value);
    }

    /// <summary>
    /// <c>factory.model-deployments.v1</c>: <c>AUTHORIZATION</c> records to <c>model_deployment</c>;
    /// <c>WATERMARK</c> records to one row, kept only when newer by sequence.
    /// </summary>
    public async Task ModelDeploymentAsync(ConsumeResult<string, byte[]> record, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(record.Message.Value);
        var r = doc.RootElement;
        var sequence = r.GetProperty("authorizationSequence"u8).GetInt64();

        if (Str(r, "recordType"u8) == "WATERMARK")
        {
            await using var watermark = db.CreateCommand("""
                INSERT INTO operations.authorization_watermark VALUES ($1, $2, $3, $4, $5, $6, $7)
                ON CONFLICT (partition_key) DO UPDATE SET
                    authorization_sequence = EXCLUDED.authorization_sequence, occurred_at_utc = EXCLUDED.occurred_at_utc,
                    issued_at_utc = EXCLUDED.issued_at_utc, source_topic = EXCLUDED.source_topic,
                    source_partition = EXCLUDED.source_partition, source_offset = EXCLUDED.source_offset
                WHERE EXCLUDED.authorization_sequence > authorization_watermark.authorization_sequence
                """);
            Add(watermark, Str(r, "partitionKey"u8), sequence, Time(r, "occurredAtUtc"u8), OptTime(r, "issuedAtUtc"u8),
                record.Topic, record.Partition.Value, record.Offset.Value);
            await watermark.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        await using var cmd = db.CreateCommand("""
            INSERT INTO operations.model_deployment VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17)
            ON CONFLICT (partition_key, model_version, authorization_sequence) DO UPDATE SET
                model_name = EXCLUDED.model_name, model_run_id = EXCLUDED.model_run_id, deployment_stage = EXCLUDED.deployment_stage,
                quarantined = EXCLUDED.quarantined, authorized_equipment_cohort = EXCLUDED.authorized_equipment_cohort,
                feature_schema_version = EXCLUDED.feature_schema_version, issued_at_utc = EXCLUDED.issued_at_utc,
                valid_until_utc = EXCLUDED.valid_until_utc, occurred_at_utc = EXCLUDED.occurred_at_utc,
                correlation_id = EXCLUDED.correlation_id, record = EXCLUDED.record, source_topic = EXCLUDED.source_topic,
                source_partition = EXCLUDED.source_partition, source_offset = EXCLUDED.source_offset
            """);
        var cohort = r.GetProperty("authorizedEquipmentCohort"u8);
        Add(cmd, Str(r, "partitionKey"u8), Str(r, "modelVersion"u8), sequence, Str(r, "modelName"u8), Str(r, "modelRunId"u8),
            Str(r, "deploymentStage"u8), r.GetProperty("quarantined"u8).GetBoolean(),
            cohort.ValueKind == JsonValueKind.Null ? null : Json(cohort),
            r.GetProperty("featureSchemaVersion"u8).GetInt32(), OptTime(r, "issuedAtUtc"u8), OptTime(r, "validUntilUtc"u8),
            Time(r, "occurredAtUtc"u8), Guid.Parse(Str(r, "correlationId"u8)), Record(record),
            record.Topic, record.Partition.Value, record.Offset.Value);
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>A partitioned write: the partition for its time first, then the row, in one transaction.</summary>
    private async Task WriteAsync(string table, DateTime at, string sql, CancellationToken cancellationToken, params object?[] values)
    {
        await using var conn = await db.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);

        await using (var ensure = new NpgsqlCommand("SELECT operations.ensure_partition($1, $2)", conn, tx))
        {
            Add(ensure, table, at);
            await ensure.ExecuteScalarAsync(cancellationToken);
        }

        await using (var cmd = new NpgsqlCommand(sql, conn, tx))
        {
            Add(cmd, values);
            await cmd.ExecuteNonQueryAsync(cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
    }

    private static void Add(NpgsqlCommand cmd, params object?[] values)
    {
        foreach (var value in values)
        {
            cmd.Parameters.Add(value switch
            {
                null => new NpgsqlParameter { Value = DBNull.Value },
                JsonText json => new NpgsqlParameter { Value = json.Text, NpgsqlDbType = NpgsqlDbType.Jsonb },
                _ => new NpgsqlParameter { Value = value },
            });
        }
    }

    /// <summary>Marks a string as JSON so it is bound as <c>jsonb</c>, not <c>text</c>.</summary>
    private sealed record JsonText(string Text);

    private static JsonText Json(JsonElement element) => new(element.GetRawText());

    private static JsonText Record(ConsumeResult<string, byte[]> record) => new(Encoding.UTF8.GetString(record.Message.Value));

    private static string Str(JsonElement e, ReadOnlySpan<byte> name) => e.GetProperty(name).GetString()!;

    private static string? OptStr(JsonElement e, ReadOnlySpan<byte> name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static Guid? OptGuid(JsonElement e, ReadOnlySpan<byte> name) => OptStr(e, name) is { } s ? Guid.Parse(s) : null;

    private static bool? OptBool(JsonElement e, ReadOnlySpan<byte> name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    /// <summary>A nullable measurement: null is "no trustworthy value" (ADR-0018), stored as null, never zero.</summary>
    private static double? Num(JsonElement e, ReadOnlySpan<byte> name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static DateTime Time(JsonElement e, ReadOnlySpan<byte> name) =>
        DateTime.Parse(Str(e, name), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    private static DateTime? OptTime(JsonElement e, ReadOnlySpan<byte> name) =>
        OptStr(e, name) is { } s ? DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal) : null;
}
