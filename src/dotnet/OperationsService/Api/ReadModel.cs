using System.Globalization;
using System.Text;
using System.Text.Json;
using Npgsql;

namespace Mair.OperationsService.Api;

/// <summary>
/// The read side of <c>OPERATIONS_API.md</c> §10a: one method per route, each the mapping table
/// written as SQL, each writing the response body exactly as the contract shapes it. "Latest" is
/// always greatest <c>occurred_at_utc</c>, then greatest provenance offset.
/// </summary>
public sealed class ReadModel(NpgsqlDataSource db)
{
    /// <summary>The latest of each source per current equipment, as lateral joins.</summary>
    private const string Summary = """
        SELECT s.equipment_id, s.state, s.ai_eligible, s.occurred_at_utc,
               o.resulting_mode, (o.record->>'requestedTargetPct')::double precision, (o.record->>'appliedTargetPct')::double precision,
               o.control_epoch, o.occurred_at_utc,
               r.quality_overall, r.quality_flags::text, r.temperature_c, r.vibration_rms, r.current_a, r.voltage_v, r.rpm,
               r.torque_nm, r.operation_rate_pct, r.occurred_at_utc,
               p.anomaly_score, p.failure_probability, p.rul_minutes, p.model_name, p.model_version, p.deployment_stage, p.occurred_at_utc,
               d.decision, d.reason_codes::text, d.occurred_at_utc
        FROM operations.current_equipment_state s
        LEFT JOIN LATERAL (SELECT * FROM operations.control_outcome x WHERE x.equipment_id = s.equipment_id
                           ORDER BY x.occurred_at_utc DESC, x.source_offset DESC LIMIT 1) o ON true
        LEFT JOIN LATERAL (SELECT * FROM operations.telemetry_reading_1s x WHERE x.equipment_id = s.equipment_id
                           ORDER BY x.occurred_at_utc DESC, x.source_offset DESC LIMIT 1) r ON true
        LEFT JOIN LATERAL (SELECT * FROM operations.prediction x WHERE x.equipment_id = s.equipment_id
                           ORDER BY x.occurred_at_utc DESC, x.source_offset DESC LIMIT 1) p ON true
        LEFT JOIN LATERAL (SELECT * FROM operations.safety_decision x WHERE x.equipment_id = s.equipment_id
                           ORDER BY x.occurred_at_utc DESC, x.source_offset DESC LIMIT 1) d ON true
        """;

    private static readonly string[] Channels = ["temperatureC", "vibrationRms", "currentA", "voltageV", "rpm", "torqueNm", "operationRatePct"];

    public async Task<byte[]> EquipmentListAsync(int limit, string? after, string? state, string? controlMode, CancellationToken ct)
    {
        // Summary ends at "ON true" with no trailing whitespace: the clause must start on its own line.
        await using var cmd = db.CreateCommand(Summary + Environment.NewLine + """
            WHERE ($1::text IS NULL OR s.equipment_id > $1) AND ($2::text IS NULL OR s.state = $2)
              AND ($3::text IS NULL OR o.resulting_mode = $3)
            ORDER BY s.equipment_id LIMIT $4
            """);
        Add(cmd, after, state, controlMode, limit + 1);

        return await JsonAsync(async w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("items"u8);
            string? last = null;
            var count = 0;
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                var more = false;
                while (await reader.ReadAsync(ct))
                {
                    if (count == limit)
                    {
                        // The query asked for one row past the page: its existence is the next page.
                        more = true;
                        break;
                    }

                    WriteSummary(w, reader, detail: false);
                    last = reader.GetString(0);
                    count++;
                }

                w.WriteEndArray();
                WriteCursor(w, more ? Cursor.Encode(new Dictionary<string, string> { ["e"] = last! }) : null);
            }

            w.WriteEndObject();
        });
    }

    /// <returns><c>null</c> when the equipment is not current.</returns>
    public async Task<byte[]?> EquipmentDetailAsync(string equipmentId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand(Summary + Environment.NewLine + "WHERE s.equipment_id = $1");
        Add(cmd, equipmentId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return await JsonAsync(w =>
        {
            WriteSummary(w, reader, detail: true);
            return Task.CompletedTask;
        });
    }

    public async Task<byte[]?> DecisionsAsync(string equipmentId, DateTime? from, DateTime? to, string? decision, int limit, (DateTime At, Guid Id)? after, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT record::text, occurred_at_utc, decision_id FROM operations.safety_decision
            WHERE equipment_id = $1 AND ($2::timestamptz IS NULL OR occurred_at_utc >= $2) AND ($3::timestamptz IS NULL OR occurred_at_utc < $3)
              AND ($4::text IS NULL OR decision = $4) AND ($5::timestamptz IS NULL OR (occurred_at_utc, decision_id) < ($5, $6::uuid))
            ORDER BY occurred_at_utc DESC, decision_id DESC LIMIT $7
            """);
        Add(cmd, equipmentId, from, to, decision, after?.At, after?.Id, limit + 1);
        return await PageAsync(cmd, equipmentId, limit, WriteDecision, ct);
    }

    public async Task<byte[]?> CommandsAsync(string equipmentId, DateTime? from, DateTime? to, int limit, (DateTime At, Guid Id)? after, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT record::text, occurred_at_utc, command_id FROM operations.control_outcome
            WHERE equipment_id = $1 AND ($2::timestamptz IS NULL OR occurred_at_utc >= $2) AND ($3::timestamptz IS NULL OR occurred_at_utc < $3)
              AND ($4::timestamptz IS NULL OR (occurred_at_utc, command_id) < ($4, $5::uuid))
            ORDER BY occurred_at_utc DESC, command_id DESC LIMIT $6
            """);
        Add(cmd, equipmentId, from, to, after?.At, after?.Id, limit + 1);
        return await PageAsync(cmd, equipmentId, limit, WriteCommand, ct);
    }

    /// <summary>
    /// <c>AC-047</c>: one statement. The prediction minted the correlation ID (OD-013); every decision
    /// and outcome carrying it, oldest first; the readings of the prediction's window.
    /// </summary>
    /// <returns><c>null</c> when nothing carries the correlation ID.</returns>
    public async Task<byte[]?> TraceAsync(Guid correlationId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            WITH p AS (SELECT * FROM operations.prediction WHERE correlation_id = $1 ORDER BY occurred_at_utc, source_offset LIMIT 1)
            SELECT (SELECT record::text FROM p),
                   (SELECT window_id FROM p), (SELECT equipment_id FROM p), (SELECT window_start_utc FROM p), (SELECT window_end_utc FROM p),
                   (SELECT coalesce(jsonb_agg(record ORDER BY occurred_at_utc, decision_id), '[]'::jsonb)::text
                      FROM operations.safety_decision WHERE correlation_id = $1),
                   (SELECT coalesce(jsonb_agg(record ORDER BY occurred_at_utc, command_id), '[]'::jsonb)::text
                      FROM operations.control_outcome WHERE correlation_id = $1),
                   (SELECT coalesce(jsonb_agg(jsonb_build_object(
                               'secondUtc', to_char(r.second_utc AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"'),
                               'eventTimeUtc', to_char(r.event_time_utc AT TIME ZONE 'UTC', 'YYYY-MM-DD"T"HH24:MI:SS.MS"Z"'),
                               'sequence', r.sequence, 'qualityOverall', r.quality_overall, 'qualityFlags', r.quality_flags,
                               'measurements', jsonb_build_object(
                                   'temperatureC', r.temperature_c, 'vibrationRms', r.vibration_rms, 'currentA', r.current_a,
                                   'voltageV', r.voltage_v, 'rpm', r.rpm, 'torqueNm', r.torque_nm, 'operationRatePct', r.operation_rate_pct))
                             ORDER BY r.second_utc), '[]'::jsonb)::text
                      FROM operations.telemetry_reading_1s r JOIN p ON r.equipment_id = p.equipment_id
                     WHERE r.second_utc >= p.window_start_utc AND r.second_utc < p.window_end_utc)
            """);
        Add(cmd, correlationId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);

        var prediction = reader.IsDBNull(0) ? null : reader.GetString(0);
        using var decisions = JsonDocument.Parse(reader.GetString(5));
        using var commands = JsonDocument.Parse(reader.GetString(6));
        if (prediction is null && decisions.RootElement.GetArrayLength() == 0 && commands.RootElement.GetArrayLength() == 0)
        {
            return null;
        }

        return await JsonAsync(w =>
        {
            w.WriteStartObject();
            w.WriteString("correlationId"u8, correlationId);
            w.WritePropertyName("window"u8);
            if (prediction is null)
            {
                w.WriteNullValue();
            }
            else
            {
                w.WriteStartObject();
                w.WriteString("windowId"u8, reader.GetGuid(1));
                w.WriteString("equipmentId"u8, reader.GetString(2));
                WriteTime(w, "startUtc"u8, reader.GetDateTime(3));
                WriteTime(w, "endUtc"u8, reader.GetDateTime(4));
                w.WritePropertyName("readings"u8);
                w.WriteRawValue(reader.GetString(7));
                w.WriteEndObject();
            }

            w.WritePropertyName("prediction"u8);
            if (prediction is null)
            {
                w.WriteNullValue();
            }
            else
            {
                w.WriteRawValue(prediction);
            }

            w.WriteStartArray("decisions"u8);
            foreach (var d in decisions.RootElement.EnumerateArray())
            {
                WriteDecision(w, d);
            }

            w.WriteEndArray();
            w.WriteStartArray("commands"u8);
            foreach (var c in commands.RootElement.EnumerateArray())
            {
                WriteCommand(w, c);
            }

            w.WriteEndArray();
            w.WriteEndObject();
            return Task.CompletedTask;
        });
    }

    public async Task<byte[]> ModelsAsync(DateTimeOffset now, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT DISTINCT ON (partition_key) model_name, model_version, model_run_id, deployment_stage, quarantined,
                   authorized_equipment_cohort::text, feature_schema_version, issued_at_utc
            FROM operations.model_deployment ORDER BY partition_key, authorization_sequence DESC
            """);
        await using var watermark = db.CreateCommand(
            "SELECT occurred_at_utc FROM operations.authorization_watermark ORDER BY authorization_sequence DESC LIMIT 1");
        var lastWatermark = await watermark.ExecuteScalarAsync(ct) as DateTime?;

        return await JsonAsync(async w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("items"u8);
            await using (var reader = await cmd.ExecuteReaderAsync(ct))
            {
                while (await reader.ReadAsync(ct))
                {
                    w.WriteStartObject();
                    w.WriteString("modelName"u8, reader.GetString(0));
                    w.WriteString("modelVersion"u8, reader.GetString(1));
                    w.WriteString("modelRunId"u8, reader.GetString(2));
                    w.WriteString("deploymentStage"u8, reader.GetString(3));
                    w.WriteBoolean("quarantined"u8, reader.GetBoolean(4));
                    w.WritePropertyName("authorizedEquipmentCohort"u8);
                    if (reader.IsDBNull(5))
                    {
                        w.WriteNullValue();
                    }
                    else
                    {
                        w.WriteRawValue(reader.GetString(5));
                    }

                    w.WriteNumber("featureSchemaVersion"u8, reader.GetInt32(6));
                    if (!reader.IsDBNull(7))
                    {
                        WriteTime(w, "issuedAtUtc"u8, reader.GetDateTime(7));
                    }

                    w.WriteEndObject();
                }
            }

            w.WriteEndArray();
            if (lastWatermark is { } at)
            {
                w.WriteNumber("watermarkAgeSeconds"u8, Math.Round((now.UtcDateTime - at).TotalSeconds, 3));
            }
            else
            {
                w.WriteNull("watermarkAgeSeconds"u8);
            }

            w.WriteEndObject();
        });
    }

    /// <summary>Percentage of current equipment whose latest outcome left it in SAFE_FALLBACK; <c>null</c> if there is none.</summary>
    public async Task<double?> FallbackRatePctAsync(CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT count(*) FILTER (WHERE o.resulting_mode = 'SAFE_FALLBACK'), count(*)
            FROM operations.current_equipment_state s
            LEFT JOIN LATERAL (SELECT resulting_mode FROM operations.control_outcome x WHERE x.equipment_id = s.equipment_id
                               ORDER BY x.occurred_at_utc DESC, x.source_offset DESC LIMIT 1) o ON true
            """);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        await reader.ReadAsync(ct);
        var total = reader.GetInt64(1);
        return total == 0 ? null : Math.Round(100.0 * reader.GetInt64(0) / total, 3);
    }

    private async Task<byte[]?> PageAsync(NpgsqlCommand cmd, string equipmentId, int limit, Action<Utf8JsonWriter, JsonElement> write, CancellationToken ct)
    {
        var rows = new List<(string Record, DateTime At, Guid Id)>();
        await using (var reader = await cmd.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                rows.Add((reader.GetString(0), reader.GetDateTime(1), reader.GetGuid(2)));
            }
        }

        if (rows.Count == 0 && !await KnownAsync(equipmentId, ct))
        {
            return null;
        }

        var more = rows.Count > limit;
        var page = rows.Take(limit).ToList();
        return await JsonAsync(w =>
        {
            w.WriteStartObject();
            w.WriteStartArray("items"u8);
            foreach (var row in page)
            {
                using var doc = JsonDocument.Parse(row.Record);
                write(w, doc.RootElement);
            }

            w.WriteEndArray();
            var last = page.LastOrDefault();
            WriteCursor(w, more ? Cursor.Encode(new Dictionary<string, string>
            {
                ["t"] = last.At.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
                ["i"] = last.Id.ToString("D"),
            }) : null);
            w.WriteEndObject();
            return Task.CompletedTask;
        });
    }

    /// <summary>Audit routes answer 404 only for an equipment with no row anywhere (§10a, AC-029).</summary>
    private async Task<bool> KnownAsync(string equipmentId, CancellationToken ct)
    {
        await using var cmd = db.CreateCommand("""
            SELECT EXISTS (SELECT 1 FROM operations.equipment_state_history WHERE equipment_id = $1)
                OR EXISTS (SELECT 1 FROM operations.safety_decision WHERE equipment_id = $1)
                OR EXISTS (SELECT 1 FROM operations.control_outcome WHERE equipment_id = $1)
            """);
        Add(cmd, equipmentId);
        return (bool)(await cmd.ExecuteScalarAsync(ct))!;
    }

    private static void WriteSummary(Utf8JsonWriter w, NpgsqlDataReader r, bool detail)
    {
        w.WriteStartObject();
        w.WriteString("equipmentId"u8, r.GetString(0));
        w.WriteString("state"u8, r.GetString(1));
        NullableString(w, "controlMode"u8, r, 4);
        w.WriteBoolean("aiEligible"u8, r.GetBoolean(2));
        NullableString(w, "qualityOverall"u8, r, 9);
        NullableNumber(w, "commandedRatePct"u8, r, 5);
        NullableNumber(w, "appliedRatePct"u8, r, 6);
        NullableNumber(w, "anomalyScore"u8, r, 19);

        // FR-050: the active rejection or fallback reason - none for an accepted recommendation.
        w.WritePropertyName("activeReasonCodes"u8);
        if (!r.IsDBNull(26) && r.GetString(26) is "REJECT" or "FALLBACK")
        {
            w.WriteRawValue(r.GetString(27));
        }
        else
        {
            w.WriteStartArray();
            w.WriteEndArray();
        }

        var updated = new[] { 3, 8, 18, 25, 28 }.Where(i => !r.IsDBNull(i)).Max(i => r.GetDateTime(i));
        WriteTime(w, "updatedAtUtc"u8, updated);

        if (detail)
        {
            w.WriteStartObject("measurements"u8);
            if (!r.IsDBNull(18))
            {
                for (var i = 0; i < Channels.Length; i++)
                {
                    NullableNumber(w, Channels[i], r, 11 + i);
                }
            }

            w.WriteEndObject();
            w.WritePropertyName("qualityFlags"u8);
            if (r.IsDBNull(10))
            {
                w.WriteStartArray();
                w.WriteEndArray();
            }
            else
            {
                w.WriteRawValue(r.GetString(10));
            }

            if (!r.IsDBNull(22))
            {
                w.WriteStartObject("model"u8);
                w.WriteString("name"u8, r.GetString(22));
                w.WriteString("version"u8, r.GetString(23));
                w.WriteString("deploymentStage"u8, r.GetString(24));
                w.WriteEndObject();
            }

            NullableNumber(w, "failureProbability"u8, r, 20);
            NullableNumber(w, "rulMinutes"u8, r, 21);
            w.WritePropertyName("controlEpoch"u8);
            if (r.IsDBNull(7))
            {
                w.WriteNullValue();
            }
            else
            {
                w.WriteNumberValue(r.GetInt64(7));
            }

            // A Control Service quantity with no projected source (§10a).
            w.WriteNull("rateBudgetRemainingPp"u8);
        }

        w.WriteEndObject();
    }

    private static void WriteDecision(Utf8JsonWriter w, JsonElement d)
    {
        w.WriteStartObject();
        Copy(w, d, "decisionId", "equipmentId", "predictionId", "decision", "reasonCodes");
        if (d.TryGetProperty("recommendation"u8, out var rec) && rec.ValueKind == JsonValueKind.Object)
        {
            Copy(w, rec, "requestedOperationRatePct", "boundedOperationRatePct", "wasClamped");
        }

        Copy(w, d, "decidedAtUtc", "correlationId");
        w.WriteEndObject();
    }

    private static void WriteCommand(Utf8JsonWriter w, JsonElement c)
    {
        w.WriteStartObject();
        Copy(w, c, "commandId", "equipmentId", "decisionId", "status", "reasonCode", "authenticatedSource", "controlEpoch",
            "appliedTargetPct", "resultingMode", "modeTransitionId", "fromMode", "autonomous", "operatorId", "occurredAtUtc");
        w.WriteEndObject();
    }

    /// <summary>The named properties the record has, as the record has them; absent stays absent.</summary>
    private static void Copy(Utf8JsonWriter w, JsonElement source, params string[] names)
    {
        foreach (var name in names)
        {
            if (source.TryGetProperty(name, out var value))
            {
                w.WritePropertyName(name);
                value.WriteTo(w);
            }
        }
    }

    private static void WriteCursor(Utf8JsonWriter w, string? cursor)
    {
        if (cursor is null)
        {
            w.WriteNull("nextCursor"u8);
        }
        else
        {
            w.WriteString("nextCursor"u8, cursor);
        }
    }

    private static void NullableString(Utf8JsonWriter w, ReadOnlySpan<byte> name, NpgsqlDataReader r, int i)
    {
        if (r.IsDBNull(i))
        {
            w.WriteNull(name);
        }
        else
        {
            w.WriteString(name, r.GetString(i));
        }
    }

    private static void NullableNumber(Utf8JsonWriter w, ReadOnlySpan<byte> name, NpgsqlDataReader r, int i)
    {
        if (r.IsDBNull(i))
        {
            w.WriteNull(name);
        }
        else
        {
            w.WriteNumber(name, r.GetDouble(i));
        }
    }

    private static void NullableNumber(Utf8JsonWriter w, string name, NpgsqlDataReader r, int i)
    {
        if (r.IsDBNull(i))
        {
            w.WriteNull(name);
        }
        else
        {
            w.WriteNumber(name, r.GetDouble(i));
        }
    }

    private static void WriteTime(Utf8JsonWriter w, ReadOnlySpan<byte> name, DateTime at) =>
        w.WriteString(name, at.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));

    private static async Task<byte[]> JsonAsync(Func<Utf8JsonWriter, Task> write)
    {
        using var stream = new MemoryStream();
        await using (var w = new Utf8JsonWriter(stream))
        {
            await write(w);
        }

        return stream.ToArray();
    }

    private static void Add(NpgsqlCommand cmd, params object?[] values)
    {
        foreach (var value in values)
        {
            cmd.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        }
    }
}

/// <summary>Opaque keyset cursors: base64url JSON of the last row's sort key (§10a).</summary>
public static class Cursor
{
    public static string Encode(Dictionary<string, string> key) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(key)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <exception cref="BadRequestException">The cursor does not decode to exactly <paramref name="fields"/>.</exception>
    public static Dictionary<string, string> Decode(string cursor, params string[] fields)
    {
        try
        {
            var text = cursor.Replace('-', '+').Replace('_', '/');
            text = text.PadRight(text.Length + ((4 - (text.Length % 4)) % 4), '=');
            var key = JsonSerializer.Deserialize<Dictionary<string, string>>(Convert.FromBase64String(text));
            if (key is null || key.Count != fields.Length || fields.Any(f => !key.ContainsKey(f)))
            {
                throw new BadRequestException("cursor has the wrong shape");
            }

            return key;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new BadRequestException("cursor does not decode");
        }
    }
}
