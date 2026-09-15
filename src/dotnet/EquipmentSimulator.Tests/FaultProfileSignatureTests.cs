namespace Mair.EquipmentSimulator.Tests;

/// <summary>
/// AC-018 — each of the 10 fault profiles produces its documented signature
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

    /// <summary>Runs at a steady 100 % rate and returns every emitted sample.</summary>
    private static List<RawSample> RunAtFullRate(EquipmentOptions options, int ticks)
    {
        var sim = new EquipmentSimulation(options);
        sim.Connect();
        sim.Tick();
        sim.WriteSetpoint(100);

        var samples = new List<RawSample>(ticks);
        for (var i = 0; i < ticks; i++)
        {
            sim.WriteSetpoint(100); // keep the dead-man fed
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
    public void BearingDegradation_RaisesVibrationAndCurrentOverTime()
    {
        var samples = RunAtFullRate(Options(FaultProfile.BearingDegradation), 18_000); // 1800 s = T_fail

        // Skip the first 100 ticks: the drive is still slewing up to 100 %, so
        // vibration there reflects the ramp rather than bearing health.
        var early = samples.Skip(100).Take(100).Average(s => s.VibrationRms!.Value);
        var late = samples.TakeLast(100).Average(s => s.VibrationRms!.Value);

        // h -> 1, so vibration -> 2.2 * 1 * (1 + 6) = 15.4 mm/s.
        Assert.InRange(early, 2.0, 2.6);
        Assert.InRange(late, 15.0, 15.8);

        // current = 12 * (1 + 0.55*h) -> 18.6 A
        Assert.InRange(samples[^1].CurrentA!.Value, 18.2, 19.0);
    }

    [Fact]
    public void Overload_MultipliesTorqueAndCurrentBy1Point6()
    {
        var normal = RunAtFullRate(Options(FaultProfile.Normal), 2_000).TakeLast(500).ToList();
        var overload = RunAtFullRate(Options(FaultProfile.Overload), 2_000).TakeLast(500).ToList();

        var torqueRatio = overload.Average(s => s.TorqueNm!.Value) / normal.Average(s => s.TorqueNm!.Value);
        var currentRatio = overload.Average(s => s.CurrentA!.Value) / normal.Average(s => s.CurrentA!.Value);

        Assert.InRange(torqueRatio, 1.58, 1.62);
        Assert.InRange(currentRatio, 1.58, 1.62);
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
    public void CommLoss_StopsAnsweringWhilePhysicsKeepsRunning()
    {
        var sim = new EquipmentSimulation(Options(FaultProfile.CommLoss));
        sim.Connect();
        Assert.NotNull(sim.Tick());
        sim.WriteSetpoint(100);

        sim.InjectCommLoss();

        for (var i = 0; i < 50; i++)
        {
            Assert.Null(sim.Tick());
        }

        // The endpoint is silent, but the equipment has not stopped: L3 still applies.
        sim.RestoreComm();
        Assert.NotNull(sim.Tick());
        Assert.True(sim.AppliedRatePct > 0);
    }
}
