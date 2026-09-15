namespace Mair.EquipmentSimulator.Tests;

/// <summary>
/// Regression tests for the findings Codex raised against commit <c>8d24190</c>
/// (`reviews/phase-1/`). Each test fails against the original implementation.
/// </summary>
public sealed class VerificationRound1Tests
{
    private static EquipmentOptions Options(
        FaultProfile profile = FaultProfile.Normal,
        SensorChannel? channel = null) => new()
    {
        EquipmentId = "eq-001",
        Seed = 17,
        FaultProfile = profile,
        FaultChannel = channel,
        DemoProfile = true,
    };

    private static EquipmentSimulation Running(EquipmentOptions options, double ratePct = 100)
    {
        var sim = new EquipmentSimulation(options);
        sim.Connect();
        sim.Tick();
        for (var i = 0; i < 100; i++)
        {
            sim.WriteSetpoint(ratePct);
            sim.Tick();
        }

        return sim;
    }

    // ---------------------------------------------------------------- COD-VFY-001

    [Fact]
    public void OperatorReset_IsRefusedWhileASafetyRequiredSensorIsStillDead()
    {
        // The reset guard used to evaluate a DIFFERENT, smaller set of conditions than the tick
        // path, so a machine faulted by a dead vibration sensor could be reset straight back to
        // IDLE and would accept setpoints for the whole 2 s re-trip window.
        var sim = Running(Options(FaultProfile.SensorDropout, SensorChannel.VibrationRms));

        Assert.Equal(EquipmentState.Fault, sim.State);
        Assert.Contains(ProtectiveCondition.SafetySensorBad, sim.LatchedConditions);

        Assert.False(sim.OperatorReset());
        Assert.Equal(EquipmentState.Fault, sim.State);
        Assert.Equal(SetpointResult.RejectedState, sim.WriteSetpoint(80));
    }

    [Fact]
    public void OperatorReset_IsRefusedWhileOverCurrentIsStillLive()
    {
        var sim = Running(Options());
        sim.InjectProtectiveCondition(ProtectiveCondition.OverCurrent);
        for (var i = 0; i < 12; i++)
        {
            sim.Tick();
        }

        Assert.Equal(EquipmentState.Fault, sim.State);
        Assert.False(sim.OperatorReset());

        sim.ClearInjectedProtectiveConditions();
        sim.Tick();

        Assert.True(sim.OperatorReset());
        Assert.Equal(EquipmentState.Idle, sim.State);
    }

    // ---------------------------------------------------------------- COD-VFY-002

    [Fact]
    public void CommLoss_CanOnlyBeInjectedIntoEquipmentConfiguredForIt()
    {
        // Otherwise the COMM_LOSS profile is inert and its AC-018 test proves only that the
        // injection method exists.
        var normal = new EquipmentSimulation(Options());

        Assert.Throws<InvalidOperationException>(() => normal.InjectCommLoss());

        var configured = new EquipmentSimulation(Options(FaultProfile.CommLoss));
        configured.Connect();
        configured.Tick();
        configured.InjectCommLoss();

        Assert.Null(configured.Tick());
    }

    // ---------------------------------------------------------------- COD-VFY-003

    [Theory]
    // No wrap: ordinary consecutive ticks.
    [InlineData(0UL, 100UL, false)]
    [InlineData(4_294_967_100UL, 4_294_967_200UL, false)]
    // The real wrap. elapsed steps 4294967200 -> 4294967300; as uint that is 4294967200 -> 4.
    [InlineData(4_294_967_200UL, 4_294_967_300UL, true)]
    // The twenty-fifth wrap, at LCM(2^32, 100) = 107374182400 ms. This is the ONLY kind of wrap the
    // discarded formulation (elapsed % 2^32 == 0) detected: at a 100 ms cadence elapsed is always a
    // multiple of 100 and 2^32 is not, so the two coincide only here.
    [InlineData(107_374_182_300UL, 107_374_182_400UL, true)]
    public void EpochWrap_IsDetectedAtTheActualUint32Rollover(ulong before, ulong after, bool expected)
    {
        Assert.Equal(expected, EquipmentSimulation.EpochWrapped(before, after));
    }

    [Fact]
    public void EpochWrap_HoldsForEveryOneOfTheFirstWraps_NotOnlyEveryTwentyFifth()
    {
        // Walk each of the first 25 rollovers at the real 100 ms step and require a detection at
        // every one. The original modulo test passed only the last of these.
        const ulong step = 100;
        const ulong span = 0x1_0000_0000UL;

        for (var wrap = 1UL; wrap <= 25; wrap++)
        {
            var boundary = wrap * span;
            var before = boundary - (boundary % step);
            if (before >= boundary)
            {
                before -= step;
            }

            Assert.True(EquipmentSimulation.EpochWrapped(before, before + step),
                $"wrap {wrap} at elapsed {before} -> {before + step} was not detected");
        }
    }

    // ---------------------------------------------------------------- COD-VFY-004

    [Fact]
    public void DriveStuckProfile_Degrades_AfterSlewGrace()
    {
        // The original code assigned _expectedPct = _appliedPct every tick, so this condition was
        // mathematically unreachable. OD-001 resolved the deeper problem - no PROFILE could cause
        // it either - by adding DRIVE_STUCK, so this test uses no injection at all.
        var sim = new EquipmentSimulation(new EquipmentOptions
        {
            EquipmentId = "eq-001",
            Seed = 17,
            FaultProfile = FaultProfile.DriveStuck,
            StuckTimeSeconds = 10,
        });

        sim.Connect();
        sim.Tick();

        // Reach 60 % and stay there until T_stuck at 10 s.
        for (var i = 0; i < 100; i++)
        {
            sim.WriteSetpoint(60);
            sim.Tick();
        }

        Assert.Equal(EquipmentState.Running, sim.State);
        Assert.Equal(60, sim.AppliedRatePct, 6);

        // The drive is frozen now. Expected climbs 60 -> 100 at 15 %/s, so the deviation passes
        // 10 pp after 0.67 s and T7 needs it sustained beyond slew_grace 5 s.
        for (var i = 0; i < 50; i++)
        {
            sim.WriteSetpoint(100);
            sim.Tick();
        }

        Assert.Equal(EquipmentState.Running, sim.State);
        Assert.Equal(60, sim.AppliedRatePct, 6);

        for (var i = 0; i < 20; i++)
        {
            sim.WriteSetpoint(100);
            sim.Tick();
        }

        Assert.Equal(EquipmentState.Degraded, sim.State);
        Assert.False(sim.IsAiEligible);
    }

    [Fact]
    public void HealthySlew_StillDoesNotDegrade()
    {
        // The counterpart: the fix must not reintroduce the cold-start problem it replaced.
        var sim = new EquipmentSimulation(Options());
        sim.Connect();
        sim.Tick();

        for (var i = 0; i < 200; i++)
        {
            sim.WriteSetpoint(100);
            sim.Tick();
            Assert.NotEqual(EquipmentState.Degraded, sim.State);
        }
    }

    // ---------------------------------------------------------------- COD-VFY-005

    [Fact]
    public void ProtectiveConditionDuringAStop_StillLatchesFault()
    {
        // T9 originally omitted STOPPING, so a machine slewing down through an over-temperature
        // condition reached IDLE without ever latching - escaping operator acknowledgement
        // whenever the condition cleared before the drive came to rest.
        var sim = Running(Options());
        Assert.Equal(SetpointResult.Accepted, sim.WriteSetpoint(0));
        sim.Tick();
        Assert.Equal(EquipmentState.Stopping, sim.State);

        sim.InjectProtectiveCondition(ProtectiveCondition.OverTemperature);
        sim.Tick();

        Assert.Equal(EquipmentState.Fault, sim.State);
        Assert.Contains(ProtectiveCondition.OverTemperature, sim.LatchedConditions);
    }

    // ---------------------------------------------------------------- COD-VFY-006

    [Fact]
    public void L3Behaviour_HoldsWithNothingButTicks()
    {
        // The assembly-reference check proves the simulator CAN run alone. This proves it DOES:
        // a protective trip and a dead-man revert, driven by nothing but Tick(), with no host,
        // no clock, no broker, no supervisor, and no control service in the process.
        //
        // The trip here comes from a real fault profile rather than an injection, which is only
        // possible since OD-002 - before it, no profile could reach a protective threshold
        // (Codex COD-VFY-006).
        var trip = new EquipmentSimulation(new EquipmentOptions
        {
            EquipmentId = "eq-001",
            Seed = 17,
            FaultProfile = FaultProfile.Overload,
        });

        trip.Connect();
        trip.Tick();
        for (var i = 0; i < 2_000 && trip.State != EquipmentState.Fault; i++)
        {
            trip.WriteSetpoint(100);
            trip.Tick();
        }

        Assert.Equal(EquipmentState.Fault, trip.State);
        Assert.Contains(ProtectiveCondition.OverCurrent, trip.LatchedConditions);
        Assert.Equal(0, trip.SetpointRatePct);

        var deadman = Running(Options());
        Assert.Equal(100, deadman.SetpointRatePct);

        for (var i = 0; i < 305; i++)
        {
            deadman.Tick();
        }

        Assert.Equal(60, deadman.SetpointRatePct);
        Assert.Equal(1, deadman.DeadmanRevertCount);
    }

    // ------------------------------------------------- round 2: new findings

    [Fact]
    public void ProtectiveTrip_ForcesRateToZeroEvenWhenTheDriveIsStuck()
    {
        // COD-R2-001, introduced by the round 1 fix: the frozen drive kept the applied rate up
        // while FAULT set only the setpoint, so a faulted machine kept spinning. This matters more
        // now that DRIVE_STUCK is a real profile rather than a demo hook - a stuck drive must not
        // be able to survive a protective trip.
        var sim = new EquipmentSimulation(new EquipmentOptions
        {
            EquipmentId = "eq-001",
            Seed = 17,
            FaultProfile = FaultProfile.DriveStuck,
            StuckTimeSeconds = 1,
            DemoProfile = true,
        });

        sim.Connect();
        sim.Tick();
        for (var i = 0; i < 100; i++)
        {
            sim.WriteSetpoint(100);
            sim.Tick();
        }

        Assert.True(sim.AppliedRatePct > 0);

        sim.InjectProtectiveCondition(ProtectiveCondition.OverVibration);
        sim.Tick();

        Assert.Equal(EquipmentState.Fault, sim.State);

        for (var i = 0; i < 100; i++)
        {
            sim.Tick();
        }

        Assert.Equal(0, sim.AppliedRatePct);
    }

    [Fact]
    public void ConditionInjectedBetweenTicks_StillBlocksReset()
    {
        // COD-R2-002: the reset guard reads the previous tick's evaluation, so an injection that
        // lands between ticks has to be unioned in synchronously.
        var sim = Running(Options());
        sim.InjectProtectiveCondition(ProtectiveCondition.OverVibration);
        sim.Tick();
        Assert.Equal(EquipmentState.Fault, sim.State);

        sim.ClearInjectedProtectiveConditions();
        sim.Tick(); // cache is now empty

        sim.InjectProtectiveCondition(ProtectiveCondition.OverTemperature); // no tick in between

        Assert.False(sim.OperatorReset());
        Assert.Equal(EquipmentState.Fault, sim.State);
    }

    [Fact]
    public void OverCurrent_TripsAndGuardsTheReset_WithNoInjectionAtAll()
    {
        // Codex's residual point on COD-VFY-001: the over-current regression used an INJECTED
        // condition, which the old reset guard already saw, so it never proved the real gap.
        // Since OD-002 raised the OVERLOAD factor to 2.8, a real overloaded machine draws
        // 12 x 2.8 = 33.6 A at full rate and trips on its own - no injection anywhere here.
        var sim = new EquipmentSimulation(new EquipmentOptions
        {
            EquipmentId = "eq-001",
            Seed = 17,
            FaultProfile = FaultProfile.Overload,
        });

        sim.Connect();
        sim.Tick();

        for (var i = 0; i < 2_000 && sim.State != EquipmentState.Fault; i++)
        {
            sim.WriteSetpoint(100);
            sim.Tick();
        }

        Assert.Equal(EquipmentState.Fault, sim.State);
        Assert.Contains(ProtectiveCondition.OverCurrent, sim.LatchedConditions);

        // The condition is still live on the trip tick, so the reset must refuse.
        Assert.False(sim.OperatorReset());

        // Once the drive has slewed to rest the measured current falls back under the threshold
        // and the reset is allowed - which is T10 working, not the guard failing.
        for (var i = 0; i < 200; i++)
        {
            sim.Tick();
        }

        Assert.True(sim.OperatorReset());
        Assert.Equal(EquipmentState.Idle, sim.State);
    }
}
