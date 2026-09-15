namespace Mair.EquipmentSimulator.Tests;

/// <summary>
/// AC-018 (bit-identical under a fixed seed) and PROP-03 (NFR-010).
/// </summary>
public sealed class DeterminismTests
{
    private static EquipmentOptions Options(ulong seed, FaultProfile profile = FaultProfile.BearingDegradation) => new()
    {
        EquipmentId = "eq-001",
        Seed = seed,
        FaultProfile = profile,
    };

    private static string Run(EquipmentOptions options, int ticks)
    {
        var sim = new EquipmentSimulation(options);
        sim.Connect();
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < ticks; i++)
        {
            if (i == 5)
            {
                Assert.Equal(SetpointResult.Accepted, sim.WriteSetpoint(100));
            }

            // Refresh the setpoint inside the dead-man window so the run is not
            // dominated by the revert (which is exercised by its own test).
            if (i % 100 == 0 && i > 5)
            {
                sim.WriteSetpoint(100);
            }

            sb.Append(sim.Tick()?.ToCanonicalString() ?? "<no-sample>").Append('\n');
        }

        return sb.ToString();
    }

    [Fact]
    public void SameSeed_ProducesBitIdenticalTelemetry()
    {
        var a = Run(Options(20260915), 3_000);
        var b = Run(Options(20260915), 3_000);

        Assert.Equal(a, b);
    }

    [Fact]
    public void DifferentSeed_ProducesDifferentTelemetry()
    {
        // Without this, an implementation that emitted no noise at all would pass
        // PROP-03 trivially while satisfying none of its intent.
        var a = Run(Options(20260915), 500);
        var b = Run(Options(20260916), 500);

        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Sequence_IsStrictlyMonotonic_AndRestartResetsItWithTheEpoch()
    {
        var sim = new EquipmentSimulation(Options(7));
        sim.Connect();

        ulong previous = 0;
        var first = true;
        for (var i = 0; i < 200; i++)
        {
            var sample = sim.Tick();
            Assert.NotNull(sample);
            if (!first)
            {
                Assert.True(sample.Sequence > previous, "sequence must be strictly monotonic");
            }

            previous = sample.Sequence;
            first = false;
        }

        // §14: a restart resets sequence AND sourceEpochMs together, so a consumer
        // can tell a restart from a gap.
        var restarted = new EquipmentSimulation(Options(7));
        restarted.Connect();
        var afterRestart = restarted.Tick();

        Assert.NotNull(afterRestart);
        Assert.Equal(0UL, afterRestart.Sequence);
        Assert.Equal(0u, afterRestart.SourceEpochMs);
    }
}
