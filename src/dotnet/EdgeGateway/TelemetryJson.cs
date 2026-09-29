using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mair.EdgeGateway;

/// <summary>
/// Writes a <see cref="CanonicalTelemetry"/> as the JSON <c>telemetry.schema.json</c> defines.
/// <para>
/// Written field by field rather than through a serialiser's conventions, because the contract is
/// the schema, not a C# type: enum spellings (<c>OPC_UA</c>, <c>SENSOR_MISSING</c>), the
/// <c>__event__</c> channel, and all seven measurement keys present even when <c>null</c> are all
/// schema decisions a default serialiser would get wrong. Every record the tests produce is
/// validated against the schema itself, so a drift here fails a test rather than a consumer.
/// </para>
/// </summary>
public static class TelemetryJson
{
    public static byte[] Serialize(CanonicalTelemetry t)
    {
        ArgumentNullException.ThrowIfNull(t);

        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("eventId", t.EventId);
            w.WriteString("eventType", t.EventType);
            w.WriteNumber("schemaVersion", t.SchemaVersion);
            w.WriteString("equipmentId", t.EquipmentId);
            w.WriteString("eventTimeUtc", Timestamp(t.EventTimeUtc));
            w.WriteString("ingestTimeUtc", Timestamp(t.IngestTimeUtc));
            w.WriteString("occurredAtUtc", Timestamp(t.OccurredAtUtc));
            w.WriteNumber("sequence", t.Sequence);
            w.WriteString("producer", t.Producer);
            w.WriteString("sourceProtocol", t.SourceProtocol switch
            {
                SourceProtocol.OpcUa => "OPC_UA",
                SourceProtocol.ModbusTcp => "MODBUS_TCP",
                _ => throw new ArgumentOutOfRangeException(nameof(t), t.SourceProtocol, "unknown source protocol"),
            });

            w.WriteStartObject("quality");
            w.WriteString("overall", ScreamingSnake(t.QualityOverall.ToString()));
            w.WriteStartArray("flags");

            // The schema declares `uniqueItems`: flags are a set. The same flag on the same channel
            // twice says nothing the first did not, and would make the record invalid.
            foreach (var flag in t.Flags.Distinct())
            {
                w.WriteStartObject();
                w.WriteString("channel", ChannelName(flag.Channel));
                w.WriteString("flag", ScreamingSnake(flag.Flag.ToString()));
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();

            // All seven, always: the shape is stable and the values are nullable (ADR-0018).
            w.WriteStartObject("measurements");
            Measurement(w, "temperatureC", t.TemperatureC);
            Measurement(w, "vibrationRms", t.VibrationRms);
            Measurement(w, "currentA", t.CurrentA);
            Measurement(w, "voltageV", t.VoltageV);
            Measurement(w, "rpm", t.Rpm);
            Measurement(w, "torqueNm", t.TorqueNm);
            Measurement(w, "operationRatePct", t.OperationRatePct);
            w.WriteEndObject();

            w.WriteString("equipmentState", ScreamingSnake(t.EquipmentState.ToString()));
            w.WriteString("correlationId", t.CorrelationId);
            w.WriteNull("causationId");
            w.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static void Measurement(Utf8JsonWriter w, string name, double? value)
    {
        if (value is { } v)
        {
            w.WriteNumber(name, v);
        }
        else
        {
            w.WriteNull(name);
        }
    }

    /// <summary>UTC, millisecond precision, `Z` suffix — the form every documented example uses.</summary>
    private static string Timestamp(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static string ChannelName(Channel channel) => channel switch
    {
        Channel.Event => "__event__",
        _ => char.ToLowerInvariant(channel.ToString()[0]) + channel.ToString()[1..],
    };

    /// <summary><c>TimestampSynthesised</c> → <c>TIMESTAMP_SYNTHESISED</c>.</summary>
    private static string ScreamingSnake(string pascal)
    {
        var sb = new StringBuilder(pascal.Length + 8);
        for (var i = 0; i < pascal.Length; i++)
        {
            if (i > 0 && char.IsUpper(pascal[i]))
            {
                sb.Append('_');
            }

            sb.Append(char.ToUpperInvariant(pascal[i]));
        }

        return sb.ToString();
    }
}
