namespace Mair.EquipmentSimulator;

/// <summary>
/// Equipment state machine states. EQUIPMENT_MODEL_AND_STATE.md §3.1.
/// <c>Running</c> is the only AI-eligible state.
/// </summary>
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

/// <summary>Degradation and fault profiles. EQUIPMENT_MODEL_AND_STATE.md §2.</summary>
public enum FaultProfile
{
    Normal,
    BearingDegradation,
    Overload,
    CoolingDegradation,
    SensorDrift,
    SensorNoise,
    SensorFreeze,
    SensorDropout,
    CommLoss,
    OodProfile,

    /// <summary>
    /// The drive stops following its setpoint at <c>T_stuck</c>. Added by OD-001: 3.3 makes
    /// drive-tracking failure a degradation condition, and until this profile existed nothing in
    /// the model could cause it.
    /// </summary>
    DriveStuck,
}

/// <summary>The seven published sensor channels. EQUIPMENT_MODEL_AND_STATE.md §1.2.</summary>
public enum SensorChannel
{
    TemperatureC,
    VibrationRms,
    CurrentA,
    VoltageV,
    Rpm,
    TorqueNm,
    OperationRatePct,
}

/// <summary>
/// Raw quality flags the equipment itself can raise. This is a subset of the closed
/// vocabulary in <c>telemetry.schema.json</c>; the remaining flags are gateway-scoped
/// (sequence gaps, timestamp problems, buffer drops) and are not the equipment's to set.
/// </summary>
public enum QualityFlag
{
    SensorMissing,
    SensorFrozen,
    ValueOutOfRange,
}

/// <summary>Derived quality, per the safety-required/advisory map in §4.</summary>
public enum QualityOverall
{
    Good,
    Uncertain,
    Bad,
}

/// <summary>Protective conditions that latch <c>FAULT</c>. EQUIPMENT_MODEL_AND_STATE.md §3.4.</summary>
public enum ProtectiveCondition
{
    OverTemperature,
    OverVibration,
    OverCurrent,
    SafetySensorBad,
    StopRequired,
}

/// <summary>Outcome of a setpoint write. Out-of-range writes are rejected, never clamped.</summary>
public enum SetpointResult
{
    Accepted,
    RejectedOutOfRange,
    RejectedState,
}

public readonly record struct ChannelFlag(SensorChannel Channel, QualityFlag Flag);

/// <summary>
/// One raw protocol-level sample. This is NOT canonical telemetry: normalisation,
/// timestamps, envelope metadata, and the canonical <c>quality.overall</c> derivation
/// belong to the Edge Gateway (Phase 2). The equipment reports raw values, raw flags,
/// and the identifiers only it can assign.
/// </summary>
public sealed record RawSample
{
    public required string EquipmentId { get; init; }

    /// <summary>Strictly monotonic per equipment, assigned by the EQUIPMENT so gateway-side loss is detectable.</summary>
    public required ulong Sequence { get; init; }

    /// <summary>Milliseconds since equipment start. Wraps at 2^32 (~49.7 d), and the wrap resets <see cref="Sequence"/>.</summary>
    public required uint SourceEpochMs { get; init; }

    public required EquipmentState State { get; init; }

    public double? TemperatureC { get; init; }
    public double? VibrationRms { get; init; }
    public double? CurrentA { get; init; }
    public double? VoltageV { get; init; }
    public double? Rpm { get; init; }
    public double? TorqueNm { get; init; }
    public double? OperationRatePct { get; init; }

    public required IReadOnlyList<ChannelFlag> Flags { get; init; }

    /// <summary>Raw quality as the equipment can judge it. The gateway derives the canonical value.</summary>
    public required QualityOverall RawQuality { get; init; }

    public double? this[SensorChannel channel] => channel switch
    {
        SensorChannel.TemperatureC => TemperatureC,
        SensorChannel.VibrationRms => VibrationRms,
        SensorChannel.CurrentA => CurrentA,
        SensorChannel.VoltageV => VoltageV,
        SensorChannel.Rpm => Rpm,
        SensorChannel.TorqueNm => TorqueNm,
        SensorChannel.OperationRatePct => OperationRatePct,
        _ => throw new ArgumentOutOfRangeException(nameof(channel)),
    };

    /// <summary>
    /// Stable, culture-independent rendering used by the determinism property (PROP-03).
    /// "R" round-trips the exact double, so this compares bits, not a formatted approximation.
    /// </summary>
    public string ToCanonicalString()
    {
        var sb = new System.Text.StringBuilder(256);
        sb.Append(EquipmentId).Append('|')
          .Append(Sequence).Append('|')
          .Append(SourceEpochMs).Append('|')
          .Append(State).Append('|')
          .Append(RawQuality);

        foreach (var channel in Enum.GetValues<SensorChannel>())
        {
            sb.Append('|').Append(this[channel]?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "null");
        }

        foreach (var flag in Flags)
        {
            sb.Append('|').Append(flag.Channel).Append(':').Append(flag.Flag);
        }

        return sb.ToString();
    }
}

/// <summary>A fault-lifecycle event, buffered for later publication to <c>factory.faults.v1</c>.</summary>
public sealed record FaultEvent(string EquipmentId, ulong Sequence, uint SourceEpochMs, string Kind, string Detail);
