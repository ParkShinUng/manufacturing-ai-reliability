namespace Mair.EquipmentSimulator.Tests;

/// <summary>
/// EQUIPMENT_MODEL_AND_STATE.md §3 — transitions T1–T12, the three forbidden
/// transitions, and the timeouts. RUNNING is the only AI-eligible state.
/// </summary>
public sealed class StateMachineTests
{
    private static EquipmentSimulation New(bool demo = false) => new(new EquipmentOptions
    {
        EquipmentId = "eq-001",
        Seed = 5,
        DemoProfile = demo,
    });

    private static void Tick(EquipmentSimulation sim, int count, double? refreshSetpoint = null)
    {
        for (var i = 0; i < count; i++)
        {
            if (refreshSetpoint is { } rate)
            {
                sim.WriteSetpoint(rate);
            }

            sim.Tick();
        }
    }

    private static EquipmentSimulation Idle()
    {
        var sim = New();
        sim.Connect();
        sim.Tick();
        Assert.Equal(EquipmentState.Idle, sim.State);
        return sim;
    }

    [Fact]
    public void T1_OfflineToConnecting_OnConnectAttempt()
    {
        var sim = New();
        Assert.Equal(EquipmentState.Offline, sim.State);

        sim.Connect();

        Assert.Equal(EquipmentState.Connecting, sim.State);
    }

    [Fact]
    public void T2_ConnectingToIdle_OnFirstValidTelemetryAtZeroRate()
    {
        var sim = New();
        sim.Connect();

        Assert.NotNull(sim.Tick());
        Assert.Equal(EquipmentState.Idle, sim.State);
    }

    [Fact]
    public void ConnectTimeout_ReturnsToOfflineAfterTenSeconds()
    {
        var sim = New(demo: true);
        sim.Connect();
        sim.InjectCommLoss(); // no telemetry can become valid

        Tick(sim, 100);

        Assert.Equal(EquipmentState.Offline, sim.State);
    }

    [Fact]
    public void T4_IdleToRunning_WhenAppliedRateExceedsZero()
    {
        var sim = Idle();

        Assert.Equal(SetpointResult.Accepted, sim.WriteSetpoint(80));
        sim.Tick();

        Assert.Equal(EquipmentState.Running, sim.State);
        Assert.True(sim.AppliedRatePct > 0);
    }

    [Fact]
    public void T5_And_T6_RunningToStoppingToIdle()
    {
        var sim = Idle();
        sim.WriteSetpoint(100);
        Tick(sim, 100, refreshSetpoint: 100);
        Assert.Equal(EquipmentState.Running, sim.State);

        Assert.Equal(SetpointResult.Accepted, sim.WriteSetpoint(0));
        sim.Tick();
        Assert.Equal(EquipmentState.Stopping, sim.State);

        // 100 % -> 0 at 15 %/s is 6.7 s, well inside stop_timeout 15 s.
        Tick(sim, 80);

        Assert.Equal(EquipmentState.Idle, sim.State);
        Assert.Equal(0, sim.AppliedRatePct);
    }

    [Fact]
    public void Stopping_DoesNotAcceptASetpoint()
    {
        var sim = Idle();
        sim.WriteSetpoint(100);
        Tick(sim, 100, refreshSetpoint: 100);
        sim.WriteSetpoint(0);
        sim.Tick();

        Assert.Equal(EquipmentState.Stopping, sim.State);
        Assert.Equal(SetpointResult.RejectedState, sim.WriteSetpoint(100));
    }

    [Fact]
    public void T7_And_T8_RunningToDegradedAndBackAfterAThirtySecondHold()
    {
        var sim = new EquipmentSimulation(new EquipmentOptions
        {
            EquipmentId = "eq-001",
            Seed = 5,
            FaultProfile = FaultProfile.SensorFreeze,
            FaultChannel = SensorChannel.TorqueNm, // advisory -> UNCERTAIN, not BAD
            DemoProfile = true,
        });

        sim.Connect();
        sim.Tick();
        Tick(sim, 100, refreshSetpoint: 100);

        // UNCERTAIN sustained > 2 s -> DEGRADED, and AI authority is suspended.
        Assert.Equal(EquipmentState.Degraded, sim.State);
        Assert.False(sim.IsAiEligible);

        sim.ClearInjectedSensorFault();

        // T8 requires the conditions clear for a full 30 s; 29 s is not enough.
        Tick(sim, 290, refreshSetpoint: 100);
        Assert.Equal(EquipmentState.Degraded, sim.State);

        Tick(sim, 20, refreshSetpoint: 100);
        Assert.Equal(EquipmentState.Running, sim.State);
        Assert.True(sim.IsAiEligible);
    }

    [Fact]
    public void T12_DegradedToStopping_OnSetpointZero()
    {
        var sim = new EquipmentSimulation(new EquipmentOptions
        {
            EquipmentId = "eq-001",
            Seed = 5,
            FaultProfile = FaultProfile.SensorFreeze,
            FaultChannel = SensorChannel.TorqueNm,
            DemoProfile = true,
        });

        sim.Connect();
        sim.Tick();
        Tick(sim, 100, refreshSetpoint: 100);
        Assert.Equal(EquipmentState.Degraded, sim.State);

        Assert.Equal(SetpointResult.Accepted, sim.WriteSetpoint(0));
        sim.Tick();

        Assert.Equal(EquipmentState.Stopping, sim.State);
    }

    [Fact]
    public void T11_SessionLost_MovesAnyStateToOffline()
    {
        var sim = Idle();
        sim.WriteSetpoint(100);
        Tick(sim, 100, refreshSetpoint: 100);
        Assert.Equal(EquipmentState.Running, sim.State);

        sim.SessionLost();

        Assert.Equal(EquipmentState.Offline, sim.State);
    }

    [Fact]
    public void Forbidden_OfflineToRunning_MustGoThroughConnecting()
    {
        var sim = New();
        Assert.Equal(EquipmentState.Offline, sim.State);

        Assert.Equal(SetpointResult.RejectedState, sim.WriteSetpoint(100));
        Tick(sim, 50);

        Assert.Equal(EquipmentState.Offline, sim.State);
    }

    [Fact]
    public void OnlyRunningIsAiEligible()
    {
        var sim = New();
        Assert.False(sim.IsAiEligible); // OFFLINE

        sim.Connect();
        Assert.False(sim.IsAiEligible); // CONNECTING

        sim.Tick();
        Assert.False(sim.IsAiEligible); // IDLE

        sim.WriteSetpoint(100);
        Tick(sim, 100, refreshSetpoint: 100);
        Assert.True(sim.IsAiEligible); // RUNNING

        sim.WriteSetpoint(0);
        sim.Tick();
        Assert.False(sim.IsAiEligible); // STOPPING
    }

    [Fact]
    public void RestartComesUpIdle_NeverRunning()
    {
        // §14: a simulator that restarted already running would hide a startup race.
        var sim = New();
        sim.Connect();
        sim.Tick();

        Assert.Equal(EquipmentState.Idle, sim.State);
        Assert.Equal(0, sim.AppliedRatePct);
    }
}
