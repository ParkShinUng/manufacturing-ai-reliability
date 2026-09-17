using Sim = Mair.EquipmentSimulator;

namespace Mair.EdgeGateway.Tests;

/// <summary>
/// <b>OT-004 / AC-023</b>, the OD-004 paired-signal matrix, run identically on both protocols.
/// <para>
/// OD-004 exists because the two protocols disagreed here: Modbus carried <c>sourceEpochMs</c> and
/// OPC UA had no restart signal at all, so the same equipment read two ways would have produced
/// different <c>SEQUENCE_GAP</c> flags after every restart. Running one matrix against both paths
/// is what proves the asymmetry is gone.
/// </para>
/// </summary>
public sealed class SequenceEpochMatrixTests
{
    /// <summary>Which protocol path the case is driven through.</summary>
    public enum Path
    {
        Modbus,
        OpcUa,
    }

    private static readonly Channel[] Measurements =
    [
        Channel.TemperatureC, Channel.VibrationRms, Channel.CurrentA, Channel.VoltageV,
        Channel.Rpm, Channel.TorqueNm, Channel.OperationRatePct,
    ];

    /// <summary>Feeds one sample through the chosen protocol path, with no value anomalies.</summary>
    private static CanonicalTelemetry Feed(TelemetryNormaliser gateway, Path path, ulong sequence, uint epochMs)
    {
        if (path == Path.Modbus)
        {
            var sample = new Sim.RawSample
            {
                EquipmentId = "eq-001",
                Sequence = sequence,
                SourceEpochMs = epochMs,
                State = Sim.EquipmentState.Running,
                Rpm = 1800,
                TorqueNm = 42,
                CurrentA = 12,
                VoltageV = 400,
                TemperatureC = 48,
                VibrationRms = 2.2,
                OperationRatePct = 100,
                Flags = [],
                RawQuality = Sim.QualityOverall.Good,
            };

            return gateway.Normalise(
                ModbusRegisterDecoder.Decode(Sim.ModbusRegisterEncoder.Encode(sample)));
        }

        var readings = Measurements.ToDictionary(c => c, c => (double?)(c switch
        {
            Channel.Rpm => 1800,
            Channel.TorqueNm => 42,
            Channel.CurrentA => 12,
            Channel.VoltageV => 400,
            Channel.TemperatureC => 48,
            Channel.VibrationRms => 2.2,
            _ => 100,
        }));

        return gateway.Normalise(readings, sequence, epochMs, EquipmentState.Running, DateTimeOffset.UnixEpoch);
    }

    private static TelemetryNormaliser Gateway() => new("eq-001", "edge-gateway@test");

    // Case 1 — samples lost inside a continuous epoch. This is what AC-023 is actually about.
    [Theory]
    [InlineData(Path.Modbus)]
    [InlineData(Path.OpcUa)]
    public void Case1_SequenceJumpsForward_IsAGap(Path path)
    {
        var gateway = Gateway();
        Feed(gateway, path, 100, 10_000);
        var telemetry = Feed(gateway, path, 105, 10_500);

        Assert.True(telemetry.HasFlag(Channel.Event, QualityFlag.SequenceGap));
        Assert.Equal(1, gateway.SequenceGaps);
    }

    // Case 2 — the same sample twice. A duplicate is not a gap; conflating them would make the
    // gap counter unusable for the thing it exists to measure.
    [Theory]
    [InlineData(Path.Modbus)]
    [InlineData(Path.OpcUa)]
    public void Case2_SequenceUnchanged_IsADuplicateNotAGap(Path path)
    {
        var gateway = Gateway();
        Feed(gateway, path, 100, 10_000);
        var telemetry = Feed(gateway, path, 100, 10_000);

        Assert.True(telemetry.HasFlag(Channel.Event, QualityFlag.DuplicateSuspected));
        Assert.False(telemetry.HasFlag(Channel.Event, QualityFlag.SequenceGap));
        Assert.Equal(0, gateway.SequenceGaps);
    }

    // Case 3 — both signals reset together: restart, or the 2^32 ms wrap. Not a gap.
    [Theory]
    [InlineData(Path.Modbus)]
    [InlineData(Path.OpcUa)]
    public void Case3_BothReset_IsARestartNotAGap(Path path)
    {
        var gateway = Gateway();
        Feed(gateway, path, 5_000, 500_000);
        var telemetry = Feed(gateway, path, 0, 0);

        Assert.False(telemetry.HasFlag(Channel.Event, QualityFlag.SequenceGap));
        Assert.Equal(0, gateway.SequenceGaps);
        Assert.Equal(QualityOverall.Good, telemetry.QualityOverall);
    }

    // Case 4 — sequence alone goes backwards. The signals disagree, so it is not a restart, and
    // suppressing it is exactly how real loss would hide behind the restart rule.
    [Theory]
    [InlineData(Path.Modbus)]
    [InlineData(Path.OpcUa)]
    public void Case4_SequenceResetsAlone_IsAGap(Path path)
    {
        var gateway = Gateway();
        Feed(gateway, path, 5_000, 500_000);
        var telemetry = Feed(gateway, path, 10, 500_100);

        Assert.True(telemetry.HasFlag(Channel.Event, QualityFlag.SequenceGap));
        Assert.Equal(1, gateway.SequenceGaps);
    }

    // Case 5 — the mirror. The first implementation missed this one entirely: it only looked at
    // the epoch when the sequence had already gone backwards, so an epoch that reset on its own
    // passed unreported. The documented matrix is what exposed it.
    [Theory]
    [InlineData(Path.Modbus)]
    [InlineData(Path.OpcUa)]
    public void Case5_EpochResetsAlone_IsAGap(Path path)
    {
        var gateway = Gateway();
        Feed(gateway, path, 5_000, 500_000);
        var telemetry = Feed(gateway, path, 5_001, 10);

        Assert.True(telemetry.HasFlag(Channel.Event, QualityFlag.SequenceGap));
        Assert.Equal(1, gateway.SequenceGaps);
    }

    [Fact]
    public void BothProtocolsProduceTheSameFlagsForEveryCase()
    {
        // The actual point of OD-004, asserted directly rather than inferred from the cases above
        // passing on both parameterisations.
        (ulong Sequence, uint Epoch)[] steps =
        [
            (100, 10_000),  // baseline
            (105, 10_500),  // case 1
            (105, 10_500),  // case 2
            (0, 0),         // case 3
            (1, 100),       // normal
            (0, 200),       // case 4
            (1, 10),        // case 5
        ];

        var modbus = Gateway();
        var opcUa = Gateway();

        foreach (var (sequence, epoch) in steps)
        {
            var m = Feed(modbus, Path.Modbus, sequence, epoch);
            var o = Feed(opcUa, Path.OpcUa, sequence, epoch);

            var modbusEventFlags = m.Flags
                .Where(f => f.Channel == Channel.Event && f.Flag != QualityFlag.TimestampSynthesised)
                .OrderBy(f => f.Flag)
                .ToArray();

            var opcUaEventFlags = o.Flags
                .Where(f => f.Channel == Channel.Event)
                .OrderBy(f => f.Flag)
                .ToArray();

            // TIMESTAMP_SYNTHESISED is excluded because it must legitimately differ: Modbus has no
            // source timestamp and always raises it (§2.6). Every other event-scoped flag must match.
            Assert.Equal(opcUaEventFlags, modbusEventFlags);
        }

        Assert.Equal(modbus.SequenceGaps, opcUa.SequenceGaps);
        Assert.Equal(3, modbus.SequenceGaps); // cases 1, 4 and 5
    }
}
