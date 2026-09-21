using System.Diagnostics;
using Sim = Mair.EquipmentSimulator;
using Protocols = Mair.EquipmentSimulator.Protocols;

namespace Mair.EdgeGateway.Tests;

/// <summary>
/// Real equipment behind real protocol servers, stepped on a wall-clock 100 ms model clock. Shared
/// by the tests that have to prove something through the shipping path rather than in-process.
/// </summary>
internal sealed class LiveEquipment : Protocols.IEquipmentAccess
{
    private readonly Sim.EquipmentSimulation[] _equipment;
    private readonly Sim.RawSample?[] _latest;
    private readonly Lock _gate = new();

    public LiveEquipment(int count)
    {
        _equipment = new Sim.EquipmentSimulation[count];
        _latest = new Sim.RawSample?[count];

        for (var i = 0; i < count; i++)
        {
            _equipment[i] = new Sim.EquipmentSimulation(new Sim.EquipmentOptions
            {
                EquipmentId = IdOf(i),
                Seed = (ulong)(20260918 + i),
                FaultProfile = (i % 3) switch
                {
                    0 => Sim.FaultProfile.Normal,
                    1 => Sim.FaultProfile.BearingDegradation,
                    _ => Sim.FaultProfile.CoolingDegradation,
                },
            });

            _equipment[i].Connect();
        }
    }

    public static string IdOf(int index) => $"eq-{index + 1:D3}";

    public int Count => _equipment.Length;

    public IReadOnlyList<string> Ids => Enumerable.Range(0, Count).Select(IdOf).ToList();

    public long TicksApplied { get; private set; }

    /// <summary>
    /// Steps issued less than one OPC UA sampling interval (50 ms) after the previous one - the clock
    /// catching up after a stall. §1.3: such steps cannot all be sampled, so a gap there is the rig's
    /// burst being detected, not the subscription losing data.
    /// </summary>
    public long Bursts { get; private set; }

    public TimeSpan LongestStepGap { get; private set; }

    /// <summary>When each burst step was issued, so a gap can be checked against the bursts around it.</summary>
    public List<DateTimeOffset> BurstTimes { get; } = [];

    /// <summary>One model step for every equipment. Driven by <see cref="RunClockAsync"/>, not a timer here.</summary>
    public void Tick()
    {
        lock (_gate)
        {
            for (var i = 0; i < _equipment.Length; i++)
            {
                _equipment[i].WriteSetpoint(100);
                _latest[i] = _equipment[i].Tick() ?? _latest[i];
            }

            TicksApplied++;
        }
    }

    public Sim.RawSample? Latest(int equipmentIndex)
    {
        lock (_gate)
        {
            return _latest[equipmentIndex];
        }
    }

    public Sim.SetpointResult WriteSetpoint(int equipmentIndex, double ratePct)
    {
        lock (_gate)
        {
            return _equipment[equipmentIndex].WriteSetpoint(ratePct);
        }
    }

    /// <summary>
    /// The model clock: real 100 ms steps, anchored so it cannot drift. <paramref name="publish"/> is
    /// read on every step so a test can swap the OPC UA server underneath a running clock.
    /// </summary>
    public Task RunClockAsync(Func<Protocols.OpcUaServerHost?> publish, CancellationToken cancellationToken)
        => Task.Run(async () =>
        {
            var next = Stopwatch.GetTimestamp();
            long? previous = null;
            while (!cancellationToken.IsCancellationRequested)
            {
                var now = Stopwatch.GetTimestamp();
                if (previous is { } p)
                {
                    var spacing = Stopwatch.GetElapsedTime(p, now);
                    if (spacing < TimeSpan.FromMilliseconds(50))
                    {
                        Bursts++;
                        BurstTimes.Add(DateTimeOffset.UtcNow);
                    }

                    if (spacing > LongestStepGap)
                    {
                        LongestStepGap = spacing;
                    }
                }

                previous = now;
                Tick();
                publish()?.NodeManager.RefreshAll();

                next += (long)(Stopwatch.Frequency * 0.1);
                var wait = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), next);
                if (wait <= TimeSpan.Zero)
                {
                    // Late: re-anchor rather than catch up. Catching up issued steps a millisecond
                    // apart after every stall, which no machine does, and which §1.3 says the 50 ms
                    // sampling cannot all see. The first LOAD-001 run failed on exactly that: 60 of
                    // 60 OPC UA gaps fell within 3 s of such a burst, with nothing dropped by the
                    // gateway. The gateway's own poll loop re-anchors for the same reason.
                    next = Stopwatch.GetTimestamp();
                }
                else
                {
                    try
                    {
                        await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }, CancellationToken.None);
}

internal static class LiveRig
{
    private static readonly Channel[] Measurements =
    [
        Channel.TemperatureC, Channel.VibrationRms, Channel.CurrentA, Channel.VoltageV,
        Channel.Rpm, Channel.TorqueNm, Channel.OperationRatePct,
    ];

    public static int FreePort()
    {
        // OPC UA base addresses are resolved from the configured URL, so port 0 cannot be used the
        // way it can with a raw TcpListener. Borrow a free one and release it immediately.
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public static string NewPkiRoot(string tag) => Path.Combine(Path.GetTempPath(), $"mair-{tag}-" + Guid.NewGuid().ToString("N"));

    public static void DeleteQuietly(string directory)
    {
        try
        {
            Directory.Delete(directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp PKI directory is not worth failing a test over.
        }
        catch (UnauthorizedAccessException)
        {
            // Same.
        }
    }

    /// <summary>
    /// FR-006: every event carries its mandatory metadata, a null value is always explained by a
    /// flag on its channel, and <c>quality.overall</c> is derived, never free-form (DEC-008).
    /// </summary>
    public static void AssertCanonical(CanonicalTelemetry telemetry)
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
            if (telemetry[channel] is { } value)
            {
                Assert.True(double.IsFinite(value));
            }
            else
            {
                // Null without a flag is an unexplained hole, which ADR-0018 forbids as firmly as a
                // fabricated number.
                Assert.Contains(telemetry.Flags, f => f.Channel == channel);
            }
        }

        Assert.Equal(Quality.Derive(telemetry.Flags), telemetry.QualityOverall);
    }
}
