namespace Mair.EquipmentSimulator.Tests;

/// <summary>
/// AC-020 / FR-035 — equipment protective conditions latch <c>FAULT</c> and force rate 0
/// with the entire platform stopped. This is layer L3 (DEC-001, ADR-0011): it must hold
/// with no Safety Supervisor, no Control Service, and no Kafka in the process.
/// </summary>
public sealed class ProtectiveConditionTests
{
    private static EquipmentSimulation Running(FaultProfile profile = FaultProfile.Normal, double ratePct = 100)
    {
        var sim = new EquipmentSimulation(new EquipmentOptions
        {
            EquipmentId = "eq-001",
            Seed = 11,
            FaultProfile = profile,
            DemoProfile = true, // protective-condition injection is demo-gated (§16)
        });

        sim.Connect();
        sim.Tick();
        sim.WriteSetpoint(ratePct);
        for (var i = 0; i < 100; i++)
        {
            sim.WriteSetpoint(ratePct);
            sim.Tick();
        }

        Assert.Equal(EquipmentState.Running, sim.State);
        return sim;
    }

    /// <summary>Ticks until <paramref name="predicate"/> holds, or fails the test.</summary>
    private static void TickUntil(EquipmentSimulation sim, Func<bool> predicate, int maxTicks, string what)
    {
        for (var i = 0; i < maxTicks && !predicate(); i++)
        {
            sim.WriteSetpoint(100);
            sim.Tick();
        }

        Assert.True(predicate(), $"{what} did not happen within {maxTicks} ticks");
    }

    [Fact]
    public void L3RunsWithNoPlatformDependencyAtAll()
    {
        // The "entire platform stopped" half of AC-020 is a structural property:
        // the simulator assembly references nothing but the framework, so there is
        // no component whose absence could disable the protective trip.
        var external = typeof(EquipmentSimulation).Assembly
            .GetReferencedAssemblies()
            .Select(a => a.Name!)
            .Where(n => !n.StartsWith("System", StringComparison.Ordinal)
                        && !n.Equals("netstandard", StringComparison.Ordinal)
                        && !n.Equals("mscorlib", StringComparison.Ordinal))
            .ToList();

        Assert.Empty(external);
    }

    [Fact]
    public void OverTemperature_LatchesFaultAndForcesRateZero()
    {
        // Cooling degradation drives T_target to 22 + 32/(1-0.675) = 120 degC and beyond.
        var sim = Running(FaultProfile.CoolingDegradation);

        // The asymptote is 22 + 32/(1 - 0.75*0.9) = 120.46 degC, only 0.46 degC over the
        // trip point, so the crossing is slow by construction. See the note in
        // EQUIPMENT_SIMULATOR.md §11.
        TickUntil(sim, () => sim.State == EquipmentState.Fault, 60_000, "over-temperature trip");

        Assert.Contains(ProtectiveCondition.OverTemperature, sim.LatchedConditions);
        Assert.Equal(0, sim.SetpointRatePct);

        // Rate is forced to zero and the drive slews down; it does not stay at speed.
        for (var i = 0; i < 100; i++)
        {
            sim.Tick();
        }

        Assert.Equal(0, sim.AppliedRatePct);
    }

    [Fact]
    public void OverVibration_LatchesFault()
    {
        // Bearing degradation drives vibration to 15.4 mm/s at h = 1, which is under
        // the 25 mm/s trip, so the condition is injected directly instead.
        var sim = Running();
        sim.InjectProtectiveCondition(ProtectiveCondition.OverVibration);
        sim.Tick();

        Assert.Equal(EquipmentState.Fault, sim.State);
        Assert.Contains(ProtectiveCondition.OverVibration, sim.LatchedConditions);
    }

    [Fact]
    public void OverCurrent_MustBeSustainedForOneSecondBeforeItTrips()
    {
        var sim = Running();
        sim.InjectProtectiveCondition(ProtectiveCondition.OverCurrent);

        // 1 s = 10 ticks. A momentary excursion must not trip.
        for (var i = 0; i < 9; i++)
        {
            sim.Tick();
            Assert.NotEqual(EquipmentState.Fault, sim.State);
        }

        sim.Tick();
        sim.Tick();
        Assert.Equal(EquipmentState.Fault, sim.State);
        Assert.Contains(ProtectiveCondition.OverCurrent, sim.LatchedConditions);
    }

    [Fact]
    public void BadQualityOnASafetyRequiredSensor_TripsAfterTwoSeconds()
    {
        // A null safety-required channel is BAD by the §4 mapping, not UNCERTAIN.
        var sim = new EquipmentSimulation(new EquipmentOptions
        {
            EquipmentId = "eq-001",
            Seed = 11,
            FaultProfile = FaultProfile.SensorDropout,
            FaultChannel = SensorChannel.VibrationRms,
        });

        sim.Connect();
        sim.Tick();
        for (var i = 0; i < 19; i++)
        {
            sim.WriteSetpoint(100);
            sim.Tick();
        }

        Assert.NotEqual(EquipmentState.Fault, sim.State);

        for (var i = 0; i < 5; i++)
        {
            sim.Tick();
        }

        Assert.Equal(EquipmentState.Fault, sim.State);
        Assert.Contains(ProtectiveCondition.SafetySensorBad, sim.LatchedConditions);
    }

    [Fact]
    public void StopRequired_IsAProtectiveCondition()
    {
        var sim = Running();
        sim.RequestStop();
        sim.Tick();

        Assert.Equal(EquipmentState.Fault, sim.State);
        Assert.Contains(ProtectiveCondition.StopRequired, sim.LatchedConditions);
    }

    [Fact]
    public void Fault_IsLatched_AndRejectsEverySetpointUntilAnOperatorResets()
    {
        var sim = Running();
        sim.InjectProtectiveCondition(ProtectiveCondition.OverVibration);
        sim.Tick();
        Assert.Equal(EquipmentState.Fault, sim.State);

        // Forbidden transition: FAULT -> RUNNING. It is latched even after the
        // condition itself clears; only T10 exits, and only through IDLE.
        sim.ClearInjectedProtectiveConditions();
        for (var i = 0; i < 600; i++)
        {
            sim.Tick();
        }

        Assert.Equal(EquipmentState.Fault, sim.State);
        Assert.Equal(SetpointResult.RejectedState, sim.WriteSetpoint(80));
        Assert.Equal(0, sim.SetpointRatePct);

        Assert.True(sim.OperatorReset());
        Assert.Equal(EquipmentState.Idle, sim.State);
    }

    [Fact]
    public void OperatorReset_IsRefusedWhileTheConditionIsStillPresent()
    {
        var sim = Running();
        sim.InjectProtectiveCondition(ProtectiveCondition.OverVibration);
        sim.Tick();

        Assert.False(sim.OperatorReset());
        Assert.Equal(EquipmentState.Fault, sim.State);
    }
}
