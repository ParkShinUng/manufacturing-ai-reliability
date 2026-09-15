namespace Mair.EquipmentSimulator.Tests;

/// <summary>
/// AC-018 — each of the 11 fault profiles produces its documented signature
/// (EQUIPMENT_MODEL_AND_STATE.md §2). <c>OOD_PROFILE</c> has its own file (AC-019).
/// </summary>
public sealed class FaultProfileSignatureTests
{
    private const ulong Seed = 4242;

    private static EquipmentOptions Options(FaultProfile profile, SensorChannel? channel = null) => new()
    {
        EquipmentId = "eq-001",
        Seed = Seed,
        FaultProfile = profile,
        FaultChannel = channel,
        DemoProfile = true, // CommLoss injection is demo-gated (EQUIPMENT_SIMULATOR.md §16)
    };

    /// <summary>Runs at a steady rate and returns every emitted sample.</summary>
    private static List<RawSample> RunAtFullRate(EquipmentOptions options, int ticks, double ratePct = 100)
    {
        var sim = new EquipmentSimulation(options);
        sim.Connect();
        sim.Tick();

        var samples = new List<RawSample>(ticks);
        for (var i = 0; i < ticks; i++)
        {
            sim.WriteSetpoint(ratePct); // also keeps the dead-man fed
            var sample = sim.Tick();
            if (sample is not null)
            {
                samples.Add(sample);
            }
        }

        return samples;
    }

    private static double StdDev(IEnumerable<double> values)
    {
        var list = values.ToList();
        var mean = list.Average();
        return Math.Sqrt(list.Sum(v => (v - mean) * (v - mean)) / list.Count);
    }

    [Fact]
    public void Normal_HoldsNominalRelationshipsWithNoiseOnly()
    {
        var samples = RunAtFullRate(Options(FaultProfile.Normal), 6_000);
        var settled = samples.TakeLast(1_000).ToList();

        // h = 0, c = 0 -> nominal values from §1.3, noise only.
        Assert.All(settled, s => Assert.InRange(s.Rpm!.Value, 1795, 1805));
        Assert.All(settled, s => Assert.InRange(s.TorqueNm!.Value, 40, 44));
        Assert.All(settled, s => Assert.InRange(s.CurrentA!.Value, 11.7, 12.3));
        Assert.All(settled, s => Assert.InRange(s.VibrationRms!.Value, 2.0, 2.4));

        // T_target = 22 + 32*1*(1+0)/(1-0) = 54 degC; after 600 s (~3.3 tau) the
        // first-order lag has closed 96 % of the gap, so ~52.8 degC.
        Assert.InRange(settled[^1].TemperatureC!.Value, 52.0, 53.5);
    }

    [Fact]
    public void BearingDegradation_RisesThroughDegradedToTheOverVibrationTrip()
    {
        // Since OD-002 the coupling is (1 + 12*h^2), so a failing bearing crosses the 12 mm/s
        // DEGRADED threshold at h ~ 0.61 and the 25 mm/s protective limit at h ~ 0.93. Before that
        // decision the worst case was 15.4 mm/s and the trip was unreachable by any profile.
        var sim = new EquipmentSimulation(Options(FaultProfile.BearingDegradation));
        sim.Connect();
        sim.Tick();

        int degradedAt = -1, faultAt = -1;
        var peakVibration = 0.0;
        double? currentAtTrip = null;

        for (var i = 0; i < 18_000 && faultAt < 0; i++)
        {
            sim.WriteSetpoint(100);
            var sample = sim.Tick();
            if (sample?.VibrationRms is { } vibration)
            {
                peakVibration = Math.Max(peakVibration, vibration);
            }

            if (degradedAt < 0 && sim.State == EquipmentState.Degraded)
            {
                degradedAt = i;
            }

            if (sim.State == EquipmentState.Fault)
            {
                faultAt = i;
                currentAtTrip = sample?.CurrentA;
            }
        }

        // h(t) = (t/1800)^2.2, so h = 0.61 at ~1437 s and h = 0.93 at ~1741 s.
        Assert.InRange(degradedAt, 14_000, 14_800);
        Assert.InRange(faultAt, 17_000, 17_900);
        Assert.True(degradedAt < faultAt, "the machine must pass through DEGRADED before it trips");
        Assert.True(peakVibration > 25.0, $"peak vibration was {peakVibration} mm/s");
        Assert.Contains(ProtectiveCondition.OverVibration, sim.LatchedConditions);

        // current = 12 * (1 + 0.55*h), about 18 A at the trip - well under its own 32 A limit, so
        // this really is the vibration path.
        Assert.InRange(currentAtTrip!.Value, 17.0, 19.0);
    }

    [Fact]
    public void Overload_MultipliesTorqueAndCurrentBy2Point8()
    {
        // At 60 % the overloaded machine draws 12 * 0.6 * 2.8 = 20.2 A and keeps running, so the
        // ratio is measurable. At 100 % it draws 33.6 A and the protective limit does its job -
        // which is the point of the OD-002 severity change.
        var normal = RunAtFullRate(Options(FaultProfile.Normal), 2_000, ratePct: 60).TakeLast(500).ToList();
        var overload = RunAtFullRate(Options(FaultProfile.Overload), 2_000, ratePct: 60).TakeLast(500).ToList();

        var torqueRatio = overload.Average(s => s.TorqueNm!.Value) / normal.Average(s => s.TorqueNm!.Value);
        var currentRatio = overload.Average(s => s.CurrentA!.Value) / normal.Average(s => s.CurrentA!.Value);

        Assert.InRange(torqueRatio, 2.75, 2.85);
        Assert.InRange(currentRatio, 2.75, 2.85);
        Assert.All(overload, s => Assert.NotEqual(EquipmentState.Fault, s.State));
    }

    [Fact]
    public void Overload_AtFullRate_TripsTheOverCurrentProtection()
    {
        var sim = new EquipmentSimulation(Options(FaultProfile.Overload));
        sim.Connect();
        sim.Tick();

        for (var i = 0; i < 200 && sim.State != EquipmentState.Fault; i++)
        {
            sim.WriteSetpoint(100);
            sim.Tick();
        }

        Assert.Equal(EquipmentState.Fault, sim.State);
        Assert.Contains(ProtectiveCondition.OverCurrent, sim.LatchedConditions);
    }

    [Fact]
    public void DriveStuck_FreezesTheAppliedRateFromTStuck()
    {
        // OD-001. The commanded setpoint keeps changing; the drive stops obeying it.
        // T_stuck must land mid-slew, or the drive has already converged and freezing it proves
        // nothing: 0 -> 100 % takes 6.7 s at 15 %/s, so freeze at 3 s, around 45 %.
        var options = Options(FaultProfile.DriveStuck) with { StuckTimeSeconds = 3 };
        var samples = RunAtFullRate(options, 600, ratePct: 100);

        var frozen = samples[100].OperationRatePct!.Value;

        Assert.InRange(frozen, 42.0, 47.0);
        Assert.Equal(frozen, samples[^1].OperationRatePct!.Value, 6);

        // The setpoint is still being commanded to 100 % every tick; the drive simply ignores it.
        Assert.True(frozen < 100);
    }

    [Fact]
    public void CoolingDegradation_RaisesTemperatureAboveNormal()
    {
        var normal = RunAtFullRate(Options(FaultProfile.Normal), 6_000);
        var degraded = RunAtFullRate(Options(FaultProfile.CoolingDegradation), 6_000);

        // c(600 s) = min(0.9, 600/900) = 0.667 -> T_target = 22 + 32/(1-0.5) = 86 degC
        Assert.True(degraded[^1].TemperatureC!.Value > normal[^1].TemperatureC!.Value + 15,
            $"cooling loss must raise temperature; got {degraded[^1].TemperatureC} vs {normal[^1].TemperatureC}");
    }

    [Fact]
    public void SensorDrift_AddsTwoPercentOfRangePerMinuteToTheChosenChannel()
    {
        var drifted = RunAtFullRate(Options(FaultProfile.SensorDrift, SensorChannel.TorqueNm), 6_000);
        var normal = RunAtFullRate(Options(FaultProfile.Normal), 6_000);

        // torqueNm range is -10..150 -> 160; 0.02 * 160 = 3.2 N.m per 60 s; 600 s -> +32.
        var delta = drifted[^1].TorqueNm!.Value - normal[^1].TorqueNm!.Value;
        Assert.InRange(delta, 30.5, 33.5);

        // Untouched channels stay nominal.
        Assert.InRange(drifted[^1].CurrentA!.Value, 11.7, 12.3);
    }

    [Fact]
    public void SensorNoise_MultipliesTheChosenChannelSigmaByEight()
    {
        var noisy = RunAtFullRate(Options(FaultProfile.SensorNoise, SensorChannel.TorqueNm), 4_000).TakeLast(2_000);
        var normal = RunAtFullRate(Options(FaultProfile.Normal), 4_000).TakeLast(2_000);

        var ratio = StdDev(noisy.Select(s => s.TorqueNm!.Value)) / StdDev(normal.Select(s => s.TorqueNm!.Value));

        Assert.InRange(ratio, 7.0, 9.0);
    }

    [Fact]
    public void SensorFreeze_HoldsTheValueAndFlagsItFrozen()
    {
        var samples = RunAtFullRate(Options(FaultProfile.SensorFreeze, SensorChannel.TorqueNm), 2_000);
        var held = samples[0].TorqueNm;

        Assert.All(samples, s => Assert.Equal(held, s.TorqueNm));
        Assert.All(samples, s => Assert.Contains(new ChannelFlag(SensorChannel.TorqueNm, QualityFlag.SensorFrozen), s.Flags));

        // The rest of the machine keeps moving, or the test would prove nothing.
        Assert.NotEqual(samples[0].CurrentA, samples[^1].CurrentA);
    }

    [Fact]
    public void SensorDropout_EmitsNullAndNeverSubstitutesAValue()
    {
        // ADR-0018 / DEC-008: a missing reading is null plus a flag, never zero,
        // never last-known, never interpolated.
        var samples = RunAtFullRate(Options(FaultProfile.SensorDropout, SensorChannel.TorqueNm), 500);

        Assert.All(samples, s => Assert.Null(s.TorqueNm));
        Assert.All(samples, s => Assert.Contains(new ChannelFlag(SensorChannel.TorqueNm, QualityFlag.SensorMissing), s.Flags));
    }

    [Fact]
    public void CommLoss_ProfileAloneStopsTheEndpointAnswering()
    {
        // No injection anywhere in this test. AC-018 asks whether the PROFILE produces its
        // documented signature, and 2's model for COMM_LOSS is "protocol endpoint stops
        // responding" (COD-VFY-002).
        var sim = new EquipmentSimulation(Options(FaultProfile.CommLoss));
        sim.Connect();

        for (var i = 0; i < 99; i++)
        {
            Assert.Null(sim.Tick());
        }

        Assert.Equal(EquipmentState.Connecting, sim.State);

        sim.Tick(); // connect_timeout 10 s
        Assert.Equal(EquipmentState.Offline, sim.State);

        // Restoring the endpoint is the other half, and it is what AC-002 will exercise in
        // Phase 2: the session comes back without restarting the equipment.
        sim.RestoreComm();
        sim.Connect();

        Assert.NotNull(sim.Tick());
        Assert.Equal(EquipmentState.Idle, sim.State);
    }
}
