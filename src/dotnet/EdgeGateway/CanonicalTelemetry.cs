namespace Mair.EdgeGateway;

public enum SourceProtocol
{
    OpcUa,
    ModbusTcp,
}

public enum EquipmentState
{
    Offline,
    Connecting,
    Idle,
    Running,
    Degraded,
    Fault,
    Stopping,
}

/// <summary>
/// A canonical telemetry event, shaped by <c>contracts/jsonschema/v1/telemetry.schema.json</c>.
/// Produced exclusively by the Edge Gateway.
/// <para>
/// All seven measurement keys are always present so the shape is stable; the <b>values</b> are
/// nullable. <c>null</c> means no trustworthy value exists and is always accompanied by a flag for
/// that channel. Substituting a synthetic, zero, or last-known value is forbidden (ADR-0018,
/// DEC-008) — a fabricated reading passes the safety gates.
/// </para>
/// </summary>
public sealed record CanonicalTelemetry
{
    public required Guid EventId { get; init; }

    public string EventType => "factory.telemetry";

    public int SchemaVersion => 1;

    public required string EquipmentId { get; init; }

    /// <summary>OPC UA SourceTimestamp where available; for Modbus this equals <see cref="IngestTimeUtc"/>.</summary>
    public required DateTimeOffset EventTimeUtc { get; init; }

    public required DateTimeOffset IngestTimeUtc { get; init; }

    /// <summary>Envelope time. For telemetry this equals <see cref="EventTimeUtc"/>.</summary>
    public DateTimeOffset OccurredAtUtc => EventTimeUtc;

    /// <summary>Assigned by the EQUIPMENT, carried through unmodified, so gateway-side loss is detectable.</summary>
    public required ulong Sequence { get; init; }

    public required string Producer { get; init; }

    public required SourceProtocol SourceProtocol { get; init; }

    public required QualityOverall QualityOverall { get; init; }

    public required IReadOnlyList<ChannelFlag> Flags { get; init; }

    public double? TemperatureC { get; init; }
    public double? VibrationRms { get; init; }
    public double? CurrentA { get; init; }
    public double? VoltageV { get; init; }
    public double? Rpm { get; init; }
    public double? TorqueNm { get; init; }
    public double? OperationRatePct { get; init; }

    public required EquipmentState EquipmentState { get; init; }

    public required Guid CorrelationId { get; init; }

    /// <summary>Always null for telemetry: it originates a causation chain rather than continuing one.</summary>
    public Guid? CausationId => null;

    public double? this[Channel channel] => channel switch
    {
        Channel.TemperatureC => TemperatureC,
        Channel.VibrationRms => VibrationRms,
        Channel.CurrentA => CurrentA,
        Channel.VoltageV => VoltageV,
        Channel.Rpm => Rpm,
        Channel.TorqueNm => TorqueNm,
        Channel.OperationRatePct => OperationRatePct,
        _ => throw new ArgumentOutOfRangeException(nameof(channel)),
    };

    public bool HasFlag(Channel channel, QualityFlag flag)
        => Flags.Contains(new ChannelFlag(channel, flag));
}

/// <summary>Valid ranges from EQUIPMENT_MODEL_AND_STATE.md §1.2, also pinned in the JSON Schema.</summary>
public static class ChannelRange
{
    public static (double Min, double Max) Of(Channel channel) => channel switch
    {
        Channel.TemperatureC => (-20, 160),
        Channel.VibrationRms => (0, 50),
        Channel.CurrentA => (0, 40),
        Channel.VoltageV => (0, 480),
        Channel.Rpm => (0, 2200),
        Channel.TorqueNm => (-10, 150),
        Channel.OperationRatePct => (0, 100),
        _ => throw new ArgumentOutOfRangeException(nameof(channel)),
    };

    public static bool Contains(Channel channel, double value)
    {
        var (min, max) = Of(channel);
        return value >= min && value <= max;
    }
}
