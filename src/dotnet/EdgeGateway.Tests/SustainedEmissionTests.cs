using Sim = Mair.EquipmentSimulator;

namespace Mair.EdgeGateway.Tests;

/// <summary>
/// <b>OT-001 / AC-001</b> — 20 simulated machines emit canonical telemetry containing all mandatory
/// metadata for 30 minutes with no unhandled simulator or gateway exception.
/// <para>
/// The 30 minutes are <b>simulated, not waited</b>. The simulator is tick-driven and reads no clock
/// (that is what makes PROP-03 checkable), so 18 000 ticks per equipment is exactly 30 minutes of
/// modelled operation and runs in seconds.
/// </para>
/// <para>
/// What this therefore does <b>not</b> prove: real-time behaviour over 30 wall-clock minutes at the
/// 100 ms cadence — scheduler jitter, GC pauses, socket stability, loop overruns under load. That is
/// <c>LOAD-001</c>, it needs a driver under <c>scripts/</c> and a report under <c>reports/</c>
/// (NFR-005, AC-042), and no performance claim may be made from this test.
/// </para>
/// </summary>
public sealed class SustainedEmissionTests
{
    private const int EquipmentCount = 20;
    private const int Ticks = 18_000; // 30 minutes at 100 ms

    private static readonly Channel[] Measurements =
    [
        Channel.TemperatureC, Channel.VibrationRms, Channel.CurrentA, Channel.VoltageV,
        Channel.Rpm, Channel.TorqueNm, Channel.OperationRatePct,
    ];

    /// <summary>A spread of profiles, so the run exercises degradation, faults and dead sensors.</summary>
    private static Sim.EquipmentOptions Options(int index)
    {
        var (profile, channel) = (index % 5) switch
        {
            0 => (Sim.FaultProfile.Normal, (Sim.SensorChannel?)null),
            1 => (Sim.FaultProfile.BearingDegradation, null),
            2 => (Sim.FaultProfile.SensorDropout, Sim.SensorChannel.TorqueNm),
            3 => (Sim.FaultProfile.CoolingDegradation, null),
            _ => (Sim.FaultProfile.SensorNoise, Sim.SensorChannel.VoltageV),
        };

        return new Sim.EquipmentOptions
        {
            EquipmentId = $"eq-{index + 1:D3}",
            Seed = (ulong)(20260918 + index),
            FaultProfile = profile,
            FaultChannel = channel,
        };
    }

    [Fact]
    public void TwentyMachinesEmitValidCanonicalTelemetryForThirtySimulatedMinutes()
    {
        var equipment = new List<Sim.EquipmentSimulation>();
        var gateways = new List<TelemetryNormaliser>();
        var sinks = new List<RecordingEgressSink>();
        var buffers = new List<BoundedEgressBuffer>();

        for (var i = 0; i < EquipmentCount; i++)
        {
            var simulation = new Sim.EquipmentSimulation(Options(i));
            simulation.Connect();
            equipment.Add(simulation);

            gateways.Add(new TelemetryNormaliser($"eq-{i + 1:D3}", "edge-gateway@ot-001"));
            sinks.Add(new RecordingEgressSink());
            buffers.Add(new BoundedEgressBuffer());
        }

        for (var tick = 0; tick < Ticks; tick++)
        {
            for (var i = 0; i < EquipmentCount; i++)
            {
                // Keep the dead-man fed; a machine that reverted to 60 % mid-run would still be
                // valid telemetry, but the run would stop exercising the full rate range.
                equipment[i].WriteSetpoint(100);

                var sample = equipment[i].Tick();
                if (sample is null)
                {
                    continue; // only a COMM_LOSS profile would do this, and none is configured
                }

                var frame = ModbusRegisterDecoder.Decode(Sim.ModbusRegisterEncoder.Encode(sample));
                buffers[i].Enqueue(gateways[i].Normalise(frame));

                while (buffers[i].TryDequeue(out var telemetry) && telemetry is not null)
                {
                    sinks[i].Emit(telemetry);
                }
            }
        }

        // Every machine produced telemetry for the whole run.
        for (var i = 0; i < EquipmentCount; i++)
        {
            Assert.Equal(Ticks, sinks[i].Records.Count);
            Assert.Equal(0, buffers[i].DroppedTotal); // a healthy sink drops nothing
        }

        // FR-006: every event carries its mandatory metadata, and the shape is stable even where a
        // value is null. Checked on all 360 000 records rather than a sample - the point of a
        // 30-minute criterion is that nothing degrades late.
        var total = 0;
        foreach (var sink in sinks)
        {
            foreach (var telemetry in sink.Records)
            {
                Assert.False(string.IsNullOrWhiteSpace(telemetry.EquipmentId));
                Assert.NotEqual(Guid.Empty, telemetry.EventId);
                Assert.NotEqual(Guid.Empty, telemetry.CorrelationId);
                Assert.Equal(1, telemetry.SchemaVersion);
                Assert.Equal("factory.telemetry", telemetry.EventType);
                Assert.False(string.IsNullOrWhiteSpace(telemetry.Producer));
                Assert.NotEqual(default, telemetry.EventTimeUtc);
                Assert.NotEqual(default, telemetry.IngestTimeUtc);
                Assert.Equal(telemetry.EventTimeUtc, telemetry.OccurredAtUtc);
                Assert.Null(telemetry.CausationId);

                foreach (var channel in Measurements)
                {
                    var value = telemetry[channel];

                    // A null value must always be accompanied by a flag for that channel: null
                    // without a flag is an unexplained hole, which is what ADR-0018 forbids as
                    // firmly as a fabricated number.
                    if (value is null)
                    {
                        Assert.Contains(telemetry.Flags, f => f.Channel == channel);
                    }
                    else
                    {
                        Assert.True(double.IsFinite(value.Value));
                    }
                }

                // quality.overall is derived, never free-form (DEC-008).
                Assert.Equal(Quality.Derive(telemetry.Flags), telemetry.QualityOverall);
                total++;
            }
        }

        Assert.Equal(EquipmentCount * Ticks, total);
    }

    [Fact]
    public void SequenceIsContinuousForEveryMachineAcrossTheRun()
    {
        // AC-023's other half at scale: over 30 simulated minutes with no induced loss, not one
        // machine may report a gap. A detector that fired spuriously would show up here rather
        // than in a short unit test.
        var equipment = new List<Sim.EquipmentSimulation>();
        var gateways = new List<TelemetryNormaliser>();

        for (var i = 0; i < EquipmentCount; i++)
        {
            var simulation = new Sim.EquipmentSimulation(Options(i));
            simulation.Connect();
            equipment.Add(simulation);
            gateways.Add(new TelemetryNormaliser($"eq-{i + 1:D3}", "edge-gateway@ot-001"));
        }

        var previous = new ulong?[EquipmentCount];

        for (var tick = 0; tick < Ticks; tick++)
        {
            for (var i = 0; i < EquipmentCount; i++)
            {
                equipment[i].WriteSetpoint(100);
                var sample = equipment[i].Tick();
                if (sample is null)
                {
                    continue;
                }

                var telemetry = gateways[i].Normalise(
                    ModbusRegisterDecoder.Decode(Sim.ModbusRegisterEncoder.Encode(sample)));

                if (previous[i] is { } last)
                {
                    Assert.Equal(last + 1, telemetry.Sequence);
                }

                previous[i] = telemetry.Sequence;
            }
        }

        Assert.All(gateways, g => Assert.Equal(0, g.SequenceGaps));
    }
}
