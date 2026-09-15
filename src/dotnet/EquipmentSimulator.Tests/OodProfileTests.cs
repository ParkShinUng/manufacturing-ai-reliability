namespace Mair.EquipmentSimulator.Tests;

/// <summary>
/// AC-019 — <c>OOD_PROFILE</c> breaks the rpm↔rate relationship while every value stays
/// in range, so a pure range check does not detect it (DEC-005, ADR-0015).
/// </summary>
public sealed class OodProfileTests
{
    private static readonly (SensorChannel Channel, double Min, double Max)[] ValidRanges =
    [
        (SensorChannel.Rpm, 0, 2200),
        (SensorChannel.TorqueNm, -10, 150),
        (SensorChannel.CurrentA, 0, 40),
        (SensorChannel.VoltageV, 0, 480),
        (SensorChannel.TemperatureC, -20, 160),
        (SensorChannel.VibrationRms, 0, 50),
        (SensorChannel.OperationRatePct, 0, 100),
    ];

    private static List<RawSample> RunAt60Percent(FaultProfile profile)
    {
        var sim = new EquipmentSimulation(new EquipmentOptions
        {
            EquipmentId = "eq-001",
            Seed = 99,
            FaultProfile = profile,
        });

        sim.Connect();
        sim.Tick();

        var samples = new List<RawSample>();
        for (var i = 0; i < 1_000; i++)
        {
            sim.WriteSetpoint(60);
            var sample = sim.Tick();
            if (sample is not null)
            {
                samples.Add(sample);
            }
        }

        return samples;
    }

    [Fact]
    public void OodProfile_HoldsRpmAt1800WhileRateIs60Percent()
    {
        var ood = RunAt60Percent(FaultProfile.OodProfile).TakeLast(200).ToList();
        var normal = RunAt60Percent(FaultProfile.Normal).TakeLast(200).ToList();

        // Learned relationship: rpm = 1800 * r. At r = 0.6 that is 1080 rpm.
        Assert.All(normal, s => Assert.InRange(s.Rpm!.Value, 1070, 1090));

        // OOD breaks it: rpm stays at full speed while the reported rate says 60 %.
        Assert.All(ood, s => Assert.InRange(s.Rpm!.Value, 1790, 1810));
        Assert.All(ood, s => Assert.InRange(s.OperationRatePct!.Value, 59.5, 60.5));
    }

    [Fact]
    public void OodProfile_KeepsEveryChannelInsideItsValidRange()
    {
        // This is the point of the profile: a range check must NOT catch it, or the
        // OOD gate is never exercised.
        var ood = RunAt60Percent(FaultProfile.OodProfile);

        foreach (var sample in ood)
        {
            foreach (var (channel, min, max) in ValidRanges)
            {
                var value = sample[channel];
                Assert.NotNull(value);
                Assert.InRange(value.Value, min, max);
            }

            Assert.DoesNotContain(sample.Flags, f => f.Flag == QualityFlag.ValueOutOfRange);
        }
    }

    [Fact]
    public void OodProfile_IsNotDetectableByTheRpmChannelAlone()
    {
        // 1800 rpm is a perfectly ordinary reading; only the rpm↔rate RELATIONSHIP
        // is anomalous. A single-channel detector sees nothing.
        var ood = RunAt60Percent(FaultProfile.OodProfile).TakeLast(200).ToList();
        var normalAtFullRate = new EquipmentSimulation(new EquipmentOptions
        {
            EquipmentId = "eq-001",
            Seed = 99,
            FaultProfile = FaultProfile.Normal,
        });

        normalAtFullRate.Connect();
        normalAtFullRate.Tick();
        RawSample? healthy = null;
        for (var i = 0; i < 1_000; i++)
        {
            normalAtFullRate.WriteSetpoint(100);
            healthy = normalAtFullRate.Tick() ?? healthy;
        }

        Assert.NotNull(healthy);
        Assert.InRange(Math.Abs(ood[^1].Rpm!.Value - healthy.Rpm!.Value), 0, 10);
    }
}
