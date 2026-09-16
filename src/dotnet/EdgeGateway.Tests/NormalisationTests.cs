using Sim = Mair.EquipmentSimulator;

namespace Mair.EdgeGateway.Tests;

/// <summary>
/// <b>OT-003 / AC-022</b> — every event validates against the contract shape, a dead sensor is
/// <c>null</c> with a flag, and no synthetic value is ever substituted.
/// <b>OT-004 / AC-023</b> — no sequence gap goes undetected, and a restart is not a gap.
/// </summary>
public sealed class NormalisationTests
{
    private static readonly Channel[] Measurements =
    [
        Channel.TemperatureC, Channel.VibrationRms, Channel.CurrentA, Channel.VoltageV,
        Channel.Rpm, Channel.TorqueNm, Channel.OperationRatePct,
    ];

    private static TelemetryNormaliser Gateway(FakeTimeProvider? time = null)
        => new("eq-001", "edge-gateway@test", time ?? new FakeTimeProvider());

    private static Sim.RawSample Sample(
        double? torque = 42, double? vibration = 2.2, ulong sequence = 1, uint epochMs = 100) => new()
    {
        EquipmentId = "eq-001",
        Sequence = sequence,
        SourceEpochMs = epochMs,
        State = Sim.EquipmentState.Running,
        Rpm = 1800,
        TorqueNm = torque,
        CurrentA = 12,
        VoltageV = 400,
        TemperatureC = 48,
        VibrationRms = vibration,
        OperationRatePct = 100,
        Flags = [],
        RawQuality = Sim.QualityOverall.Good,
    };

    private static ModbusFrame Frame(Sim.RawSample sample)
        => ModbusRegisterDecoder.Decode(Sim.ModbusRegisterEncoder.Encode(sample));

    // ------------------------------------------------------------ AC-022

    [Fact]
    public void EveryMandatoryFieldIsPresent()
    {
        // FR-006: equipment id, event timestamp, ingest timestamp, sequence, schema version,
        // quality flags - on every event.
        var telemetry = Gateway().Normalise(Frame(Sample()));

        Assert.Equal("eq-001", telemetry.EquipmentId);
        Assert.Equal("factory.telemetry", telemetry.EventType);
        Assert.Equal(1, telemetry.SchemaVersion);
        Assert.NotEqual(Guid.Empty, telemetry.EventId);
        Assert.NotEqual(Guid.Empty, telemetry.CorrelationId);
        Assert.NotEqual(default, telemetry.EventTimeUtc);
        Assert.NotEqual(default, telemetry.IngestTimeUtc);
        Assert.Equal(telemetry.EventTimeUtc, telemetry.OccurredAtUtc);
        Assert.Equal(1UL, telemetry.Sequence);
        Assert.False(string.IsNullOrWhiteSpace(telemetry.Producer));
        Assert.Null(telemetry.CausationId); // telemetry originates a causation chain
    }

    [Fact]
    public void AllSevenMeasurementKeysArePresentEvenWhenTheValueIsNull()
    {
        // The shape is stable; only the values are nullable. v0.2 made a dead sensor impossible to
        // express, so a producer had to fabricate a value or emit an invalid event.
        var telemetry = Gateway().Normalise(Frame(Sample(torque: null, vibration: null)));

        foreach (var channel in Measurements)
        {
            _ = telemetry[channel]; // an absent key would throw
        }

        Assert.Null(telemetry.TorqueNm);
        Assert.Null(telemetry.VibrationRms);
        Assert.NotNull(telemetry.Rpm);
    }

    [Fact]
    public void ADeadSensorIsNullPlusAFlag_NeverZeroNorLastKnown()
    {
        var gateway = Gateway();

        gateway.Normalise(Frame(Sample(torque: 42)));                   // a real value first
        var telemetry = gateway.Normalise(Frame(Sample(torque: null))); // then the sensor dies

        // null, and specifically NOT the 42 it read a moment ago: last-known substitution is the
        // failure mode ADR-0018 names, and it is invisible unless a real value came first.
        Assert.Null(telemetry.TorqueNm);
        Assert.True(telemetry.HasFlag(Channel.TorqueNm, QualityFlag.SensorMissing));

        // torqueNm is advisory, so a dead one degrades to UNCERTAIN rather than BAD.
        Assert.Equal(QualityOverall.Uncertain, telemetry.QualityOverall);
    }

    [Fact]
    public void ADeadSafetyRequiredSensorIsBad()
    {
        var telemetry = Gateway().Normalise(Frame(Sample(vibration: null)));

        Assert.Null(telemetry.VibrationRms);
        Assert.True(telemetry.HasFlag(Channel.VibrationRms, QualityFlag.SensorMissing));
        Assert.Equal(QualityOverall.Bad, telemetry.QualityOverall);
    }

    [Fact]
    public void AHealthySampleCarriesNoChannelFlagsAndIsGood()
    {
        // The negative case OT-003 requires. Without it an implementation that flagged every
        // channel would satisfy all the assertions above.
        var telemetry = Gateway().Normalise(Frame(Sample()));

        Assert.Equal(QualityOverall.Good, telemetry.QualityOverall);
        Assert.All(telemetry.Flags, f => Assert.Equal(Channel.Event, f.Channel));
        Assert.All(Measurements, c => Assert.NotNull(telemetry[c]));
    }

    [Fact]
    public void ModbusAlwaysRaisesTimestampSynthesised_AndItDoesNotDegradeQuality()
    {
        var telemetry = Gateway().Normalise(Frame(Sample()));

        Assert.True(telemetry.HasFlag(Channel.Event, QualityFlag.TimestampSynthesised));
        Assert.Equal(telemetry.IngestTimeUtc, telemetry.EventTimeUtc);
        Assert.Equal(SourceProtocol.ModbusTcp, telemetry.SourceProtocol);
        Assert.Equal(QualityOverall.Good, telemetry.QualityOverall);
    }

    [Fact]
    public void AnOutOfRangeReadingBecomesNull_NotAClampedValue()
    {
        // Clamping would hide a real fault behind a plausible number.
        var readings = new Dictionary<Channel, double?>
        {
            [Channel.TemperatureC] = 500, // range is -20..160
            [Channel.VibrationRms] = 2.2,
            [Channel.CurrentA] = 12,
            [Channel.VoltageV] = 400,
            [Channel.Rpm] = 1800,
            [Channel.TorqueNm] = 42,
            [Channel.OperationRatePct] = 100,
        };

        var telemetry = Gateway().Normalise(readings, 1, 100, EquipmentState.Running, DateTimeOffset.UnixEpoch);

        Assert.Null(telemetry.TemperatureC);
        Assert.True(telemetry.HasFlag(Channel.TemperatureC, QualityFlag.ValueOutOfRange));
        Assert.Equal(QualityOverall.Bad, telemetry.QualityOverall); // temperature is safety-required
    }

    // ------------------------------------------------------------ AC-023

    [Fact]
    public void ASingleSampleGapIsDetected()
    {
        var gateway = Gateway();
        gateway.Normalise(Frame(Sample(sequence: 10, epochMs: 1000)));
        var telemetry = gateway.Normalise(Frame(Sample(sequence: 12, epochMs: 1200)));

        Assert.True(telemetry.HasFlag(Channel.Event, QualityFlag.SequenceGap));
        Assert.Equal(1, gateway.SequenceGaps);
        Assert.Equal(QualityOverall.Uncertain, telemetry.QualityOverall);
    }

    [Fact]
    public void EveryGapIsCounted_NoneSilentlyMerged()
    {
        var gateway = Gateway();
        ulong[] sequences = [1, 2, 5, 6, 20, 21, 22, 40];
        uint epoch = 100;

        foreach (var s in sequences)
        {
            gateway.Normalise(Frame(Sample(sequence: s, epochMs: epoch)));
            epoch += 100;
        }

        Assert.Equal(3, gateway.SequenceGaps); // 2->5, 6->20, 22->40
    }

    [Fact]
    public void ConsecutiveSequencesRaiseNoGap()
    {
        var gateway = Gateway();
        for (ulong s = 1; s <= 200; s++)
        {
            var telemetry = gateway.Normalise(Frame(Sample(sequence: s, epochMs: (uint)(s * 100))));
            Assert.False(telemetry.HasFlag(Channel.Event, QualityFlag.SequenceGap));
        }

        Assert.Equal(0, gateway.SequenceGaps);
    }

    [Fact]
    public void ARestartIsNotAGap()
    {
        // §14: a restart resets sequence AND sourceEpochMs together. A detector that flagged every
        // sequence decrease would pass the tests above and cry wolf on every restart.
        var gateway = Gateway();
        gateway.Normalise(Frame(Sample(sequence: 5_000, epochMs: 500_000)));

        var afterRestart = gateway.Normalise(Frame(Sample(sequence: 0, epochMs: 0)));

        Assert.False(afterRestart.HasFlag(Channel.Event, QualityFlag.SequenceGap));
        Assert.Equal(0, gateway.SequenceGaps);
    }

    [Fact]
    public void ASequenceDropWithoutAnEpochResetIsStillAGap()
    {
        // The counterpart: if only the sequence goes backwards, the two signals disagree and that
        // is not a restart. Accepting it silently would let real loss hide behind the restart rule.
        var gateway = Gateway();
        gateway.Normalise(Frame(Sample(sequence: 5_000, epochMs: 500_000)));

        var telemetry = gateway.Normalise(Frame(Sample(sequence: 10, epochMs: 500_100)));

        Assert.True(telemetry.HasFlag(Channel.Event, QualityFlag.SequenceGap));
        Assert.Equal(1, gateway.SequenceGaps);
    }

    [Fact]
    public void ARepeatedSequenceIsADuplicate_NotAGap()
    {
        var gateway = Gateway();
        gateway.Normalise(Frame(Sample(sequence: 7, epochMs: 700)));
        var telemetry = gateway.Normalise(Frame(Sample(sequence: 7, epochMs: 700)));

        Assert.True(telemetry.HasFlag(Channel.Event, QualityFlag.DuplicateSuspected));
        Assert.False(telemetry.HasFlag(Channel.Event, QualityFlag.SequenceGap));
        Assert.Equal(0, gateway.SequenceGaps);
    }

    // ------------------------------------------------------------ egress

    [Fact]
    public void TheBufferDropsOldestAndCountsIt()
    {
        var buffer = new BoundedEgressBuffer(capacity: 3);
        var gateway = Gateway();

        var records = Enumerable.Range(1, 5)
            .Select(i => gateway.Normalise(Frame(Sample(sequence: (ulong)i, epochMs: (uint)(i * 100)))))
            .ToList();

        foreach (var record in records)
        {
            buffer.Enqueue(record);
        }

        Assert.Equal(3, buffer.Depth);
        Assert.Equal(2, buffer.DroppedTotal);

        // Oldest dropped, newest kept: stale telemetry is rejected by the gates anyway.
        var kept = buffer.DrainAll();
        Assert.Equal([3UL, 4UL, 5UL], kept.Select(r => r.Sequence));
    }

    [Fact]
    public void ADropIsReportedOnTheNextEvent_NeverSilently()
    {
        // The flag rides on the next event because it cannot ride on the ones that were dropped.
        var gateway = Gateway();
        var buffer = new BoundedEgressBuffer(capacity: 1, onDrop: gateway.NoteBufferOverflowDrop);

        buffer.Enqueue(gateway.Normalise(Frame(Sample(sequence: 1, epochMs: 100))));
        buffer.Enqueue(gateway.Normalise(Frame(Sample(sequence: 2, epochMs: 200)))); // drops #1

        var next = gateway.Normalise(Frame(Sample(sequence: 3, epochMs: 300)));

        Assert.True(next.HasFlag(Channel.Event, QualityFlag.BufferOverflowDrop));
        Assert.Equal(QualityOverall.Uncertain, next.QualityOverall);

        // Reported once, not on every subsequent event.
        var later = gateway.Normalise(Frame(Sample(sequence: 4, epochMs: 400)));
        Assert.False(later.HasFlag(Channel.Event, QualityFlag.BufferOverflowDrop));
    }

    [Fact]
    public void EnqueueNeverBlocksNorThrowsWhenFull()
    {
        // §13: blocking is forbidden. The caller is the OT poll loop, and a slow transport must
        // degrade into dropped events, never into missed samples.
        var buffer = new BoundedEgressBuffer(capacity: 10);
        var gateway = Gateway();

        for (var i = 1; i <= 1_000; i++)
        {
            buffer.Enqueue(gateway.Normalise(Frame(Sample(sequence: (ulong)i, epochMs: (uint)(i * 100)))));
        }

        Assert.Equal(10, buffer.Depth);
        Assert.Equal(990, buffer.DroppedTotal);
    }
}

/// <summary>A clock the tests control, so timestamps are deterministic.</summary>
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 9, 16, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        _now = _now.AddMilliseconds(100);
        return _now;
    }
}
