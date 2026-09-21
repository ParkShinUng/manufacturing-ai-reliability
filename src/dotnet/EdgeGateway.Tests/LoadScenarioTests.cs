using System.Diagnostics;
using Sim = Mair.EquipmentSimulator;
using Protocols = Mair.EquipmentSimulator.Protocols;

namespace Mair.EdgeGateway.Tests;

/// <summary>
/// <b>LOAD-001</b> — 20 equipment at 100 ms for 30 minutes, in <b>real time</b>, through the real
/// protocol stack: simulator → Modbus TCP → gateway poll loops → egress.
/// <para>
/// This is the half <c>OT-001</c> cannot prove. OT-001 compresses 30 simulated minutes into seconds
/// because the simulator is tick-driven; what it therefore says nothing about is scheduler jitter,
/// GC pauses, socket stability, and loop overruns — all of which only exist in wall-clock time.
/// </para>
/// <para>
/// <b>Skipped unless <c>MAIR_LOAD_TEST=1</c>.</b> It is not a unit test and must never run in the
/// ordinary suite. Invoke it through <c>scripts/run-load-001.mjs</c>, which captures the environment
/// and writes a report — NFR-005 and AC-042 require every performance figure to come from a script,
/// never from a number someone typed.
/// </para>
/// </summary>
public sealed class LoadScenarioTests
{
    private const int EquipmentCount = 20;

    private static bool Enabled => Environment.GetEnvironmentVariable("MAIR_LOAD_TEST") == "1";

    private static TimeSpan Duration =>
        int.TryParse(Environment.GetEnvironmentVariable("MAIR_LOAD_SECONDS"), out var s) && s > 0
            ? TimeSpan.FromSeconds(s)
            : TimeSpan.FromMinutes(30);

    private sealed class LiveEquipment : Protocols.IEquipmentAccess
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
                    EquipmentId = $"eq-{i + 1:D3}",
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

        public int Count => _equipment.Length;

        public long TicksApplied { get; private set; }

        /// <summary>One model step for every equipment. Driven by the harness, not by a timer here.</summary>
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
    }

    [SkippableFact]
    public async Task Load001_TwentyEquipmentAtTenHertz()
    {
        Skip.IfNot(Enabled, "LOAD-001 runs only under MAIR_LOAD_TEST=1; use scripts/run-load-001.mjs.");

        var duration = Duration;
        var equipment = new LiveEquipment(EquipmentCount);

        await using var server = new Protocols.ModbusServerHost(equipment, readOnlyPort: 0, writePort: 0);
        server.Start();

        using var stopping = new CancellationTokenSource();
        var loops = new List<EquipmentPollLoop>();
        var sinks = new List<RecordingEgressSink>();
        var clients = new List<ModbusTelemetryClient>();
        var normalisers = new List<TelemetryNormaliser>();
        var running = new List<Task>();

        for (var i = 0; i < EquipmentCount; i++)
        {
            var id = $"eq-{i + 1:D3}";
            var client = new ModbusTelemetryClient("127.0.0.1", server.ActualReadOnlyPort);
            var normaliser = new TelemetryNormaliser(id, "edge-gateway@load-001");
            var sink = new RecordingEgressSink();
            var loop = new EquipmentPollLoop(
                new ModbusTelemetrySource(id, i, client, normaliser),
                new BoundedEgressBuffer(),
                sink);

            clients.Add(client);
            normalisers.Add(normaliser);
            sinks.Add(sink);
            loops.Add(loop);
            running.Add(loop.RunAsync(stopping.Token));
        }

        // The model clock. Real 100 ms steps, because the point of this run is wall-clock behaviour.
        var ticker = Task.Run(async () =>
        {
            var next = Stopwatch.GetTimestamp();
            while (!stopping.Token.IsCancellationRequested)
            {
                equipment.Tick();
                next += (long)(Stopwatch.Frequency * 0.1);
                var wait = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), next);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, stopping.Token).ConfigureAwait(false);
                }
            }
        }, stopping.Token);

        var started = Stopwatch.GetTimestamp();
        try
        {
            await Task.Delay(duration, stopping.Token);
        }
        catch (OperationCanceledException)
        {
            // Nothing cancels this externally; the catch exists so a cancelled run reports rather
            // than throws.
        }

        var elapsed = Stopwatch.GetElapsedTime(started);
        await stopping.CancelAsync();

        try
        {
            await Task.WhenAll(running.Append(ticker));
        }
        catch (OperationCanceledException)
        {
            // Expected: cancellation is how the loops stop.
        }

        foreach (var client in clients)
        {
            client.Dispose();
        }

        var emitted = sinks.Sum(s => s.Records.Count);
        var gaps = normalisers.Sum(n => n.SequenceGaps);
        var overruns = loops.Sum(l => l.LoopOverruns);
        var reconnects = loops.Sum(l => l.ReconnectTotal) - EquipmentCount; // the first connect is not a reconnect

        // Written where the driver script can pick it up, because a number printed to a test log is
        // not evidence (NFR-005, AC-042).
        var summary =
            $"elapsed_seconds={elapsed.TotalSeconds:F1}\n" +
            $"equipment={EquipmentCount}\n" +
            $"model_ticks={equipment.TicksApplied}\n" +
            $"events_emitted={emitted}\n" +
            $"sequence_gaps={gaps}\n" +
            $"loop_overruns={overruns}\n" +
            $"reconnects={reconnects}\n" +
            $"events_per_second={emitted / Math.Max(1, elapsed.TotalSeconds):F1}\n" +
            $"read_failures={loops.Sum(l => l.ReadFailures)}\n" +
            $"connect_failures={loops.Sum(l => l.ConnectFailures)}\n" +
            $"last_failure={loops.Select(l => l.LastFailure).FirstOrDefault(f => f is not null) ?? "none"}\n";

        var path = Environment.GetEnvironmentVariable("MAIR_LOAD_OUTPUT");
        if (!string.IsNullOrWhiteSpace(path))
        {
            await File.WriteAllTextAsync(path, summary, TestContextCancellation);
        }

        // Assertions are the AC, not the performance figures: those belong in the report, and the
        // targets are TARGET (unmeasured) until one exists.
        Assert.True(emitted > 0, "no telemetry was emitted");
        Assert.Equal(0, gaps);                      // L-01 / D-03: no undetected loss, and none induced
        Assert.All(loops, l => Assert.True(l.ProtocolConnected));
    }

    private static CancellationToken TestContextCancellation => CancellationToken.None;
}
