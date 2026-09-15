namespace Mair.EquipmentSimulator.Tests;

/// <summary>
/// Rate-limited actuation (§1.4), the dead-man revert (DEC-001, L3), and setpoint
/// admission (EQUIPMENT_SIMULATOR.md §11).
/// </summary>
public sealed class ActuationTests
{
    private static EquipmentSimulation Idle(double safeDefaultRatePct = 60)
    {
        var sim = new EquipmentSimulation(new EquipmentOptions
        {
            EquipmentId = "eq-001",
            Seed = 3,
            SafeDefaultRatePct = safeDefaultRatePct,
        });

        sim.Connect();
        sim.Tick();
        return sim;
    }

    [Fact]
    public void AppliedRate_NeverMovesMoreThan15PercentPerSecond()
    {
        var sim = Idle();
        sim.WriteSetpoint(100);

        var previous = sim.AppliedRatePct;
        for (var i = 0; i < 200; i++)
        {
            sim.WriteSetpoint(100);
            sim.Tick();

            // 15 %/s at a 100 ms tick is 1.5 pp, with a small allowance for
            // floating-point accumulation.
            Assert.True(Math.Abs(sim.AppliedRatePct - previous) <= 1.5 + 1e-9,
                $"slewed {sim.AppliedRatePct - previous} pp in one tick");
            previous = sim.AppliedRatePct;
        }

        Assert.Equal(100, sim.AppliedRatePct, 6);
    }

    [Fact]
    public void FullRangeStart_DoesNotCountAsFailureToTrackTheSetpoint()
    {
        // The drive IS tracking during a slew; only a drive that falls behind its
        // slew-limited expected rate is degraded (§3.3).
        var sim = Idle();
        sim.WriteSetpoint(100);

        for (var i = 0; i < 100; i++)
        {
            sim.WriteSetpoint(100);
            sim.Tick();
            Assert.NotEqual(EquipmentState.Degraded, sim.State);
        }
    }

    [Fact]
    public void OutOfRangeSetpoint_IsRejected_NeverSilentlyClamped()
    {
        // §11: clamping would mask a control-path defect the Control Service bounds
        // check should have caught first.
        var sim = Idle();

        Assert.Equal(SetpointResult.RejectedOutOfRange, sim.WriteSetpoint(101));
        Assert.Equal(SetpointResult.RejectedOutOfRange, sim.WriteSetpoint(-0.1));
        Assert.Equal(SetpointResult.RejectedOutOfRange, sim.WriteSetpoint(double.NaN));
        Assert.Equal(0, sim.SetpointRatePct);
    }

    [Fact]
    public void SetpointIsIdempotent()
    {
        // This is what makes Control Service retry safe: it is a setpoint, not a delta.
        var sim = Idle();
        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(SetpointResult.Accepted, sim.WriteSetpoint(70));
        }

        Assert.Equal(70, sim.SetpointRatePct);
    }

    [Fact]
    public void NoSetpointRefreshForThirtySeconds_RevertsToTheSafeDefault()
    {
        var sim = Idle();
        sim.WriteSetpoint(100);
        for (var i = 0; i < 100; i++)
        {
            sim.Tick();
        }

        Assert.Equal(100, sim.SetpointRatePct);

        // 30 s dead-man: at 300 ticks without a write the equipment reverts itself.
        for (var i = 0; i < 205; i++)
        {
            sim.Tick();
        }

        Assert.Equal(60, sim.SetpointRatePct);
        Assert.Equal(1, sim.DeadmanRevertCount);
    }

    [Fact]
    public void DeadMan_NeverStartsAnIdleMachine()
    {
        // AI can never start a machine, and neither can a timeout. A dead-man that
        // spun up an idle drive would be a new hazard, not a mitigation.
        var sim = Idle();

        for (var i = 0; i < 1_000; i++)
        {
            sim.Tick();
        }

        Assert.Equal(EquipmentState.Idle, sim.State);
        Assert.Equal(0, sim.SetpointRatePct);
        Assert.Equal(0, sim.DeadmanRevertCount);
    }

    [Fact]
    public void DeadMan_IsRearmedByEverySetpointWrite()
    {
        var sim = Idle();
        for (var i = 0; i < 1_000; i++)
        {
            sim.WriteSetpoint(100);
            sim.Tick();
        }

        Assert.Equal(100, sim.SetpointRatePct);
        Assert.Equal(0, sim.DeadmanRevertCount);
    }

    [Fact]
    public void InvalidConfiguration_AbortsConstruction_NeverDefaults()
    {
        // NFR-014: fail closed on unvalidatable safety input.
        Assert.Throws<ArgumentException>(() => new EquipmentSimulation(new EquipmentOptions
        {
            EquipmentId = "motor-1", // violates ^eq-[0-9]{3,6}$
            Seed = 1,
        }));

        Assert.Throws<ArgumentException>(() => new EquipmentSimulation(new EquipmentOptions
        {
            EquipmentId = "eq-001",
            Seed = 1,
            FaultProfile = FaultProfile.SensorDrift, // needs a channel
        }));

        Assert.Throws<ArgumentException>(() => new EquipmentSimulation(new EquipmentOptions
        {
            EquipmentId = "eq-001",
            Seed = 1,
            SafeDefaultRatePct = 140,
        }));
    }

    [Fact]
    public void FaultInjection_IsAbsentOutsideTheDemoProfile()
    {
        // §16: fault injection must not be reachable in a non-demo build.
        var sim = Idle();

        Assert.Throws<InvalidOperationException>(
            () => sim.InjectProtectiveCondition(ProtectiveCondition.OverVibration));
        Assert.Throws<InvalidOperationException>(() => sim.InjectCommLoss());
    }
}
