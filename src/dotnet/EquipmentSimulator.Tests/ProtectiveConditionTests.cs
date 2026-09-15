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
        // Structural half of AC-020 only: this proves the simulator CAN run with nothing
        // else present - there is no component whose absence could disable the trip. It
        // does NOT prove the trips actually run in that condition (COD-VFY-006) -
        // VerificationRound1Tests.L3Behaviour_HoldsWithNothingButTicks is that half.
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
        // Since OD-002 raised the cap on c from 0.9 to 0.95, T_target is
        // 22 + 32/(1 - 0.75*0.95) = 133.3 degC against a 120 degC trip. The old 0.46 degC margin
        // made this crossing slow and fragile; it is now a comfortable 13.3 degC.
        var sim = Running(FaultProfile.CoolingDegradation);

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
        // Injected here to test the latch precisely and quickly. The PHYSICS path is
        // BearingDegradation_RisesThroughDegradedToTheOverVibrationTrip, which reaches 25 mm/s
        // from the profile alone now that OD-002 raised the health coupling to 12.0.
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
