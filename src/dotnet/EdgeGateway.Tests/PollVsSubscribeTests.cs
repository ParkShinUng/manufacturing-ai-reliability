using System.Diagnostics;
using Sim = Mair.EquipmentSimulator;
using Protocols = Mair.EquipmentSimulator.Protocols;

namespace Mair.EdgeGateway.Tests;

/// <summary>
/// Measures the claim that started this: <b>does a queued protocol actually lose fewer samples than
/// a polled one?</b>
/// <para>
/// It was asserted while both paths were in fact polling — the OPC UA client used a batch read, not
/// the §1.3 subscription — so the comparison had never been made. This runs the same equipment, the
/// same duration, the same machine, through both, and counts.
/// </para>
/// <para>
/// Skipped unless <c>MAIR_LOAD_TEST=1</c>; driven by <c>scripts/measure-poll-vs-subscribe.mjs</c>,
/// because a number that decides a contract change has to come from a script (NFR-005, AC-042).
/// </para>
/// </summary>
public sealed class PollVsSubscribeTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("MAIR_LOAD_TEST") == "1";

    private static TimeSpan Duration =>
        int.TryParse(Environment.GetEnvironmentVariable("MAIR_LOAD_SECONDS"), out var s) && s > 0
            ? TimeSpan.FromSeconds(s)
            : TimeSpan.FromSeconds(60);

    /// <summary>One equipment, ticked in real time, exposed on both protocols at once.</summary>
    private sealed class LiveEquipment : Protocols.IEquipmentAccess
    {
        private readonly Sim.EquipmentSimulation _simulation;
        private Sim.RawSample? _latest;
        private readonly Lock _gate = new();

        public LiveEquipment()
        {
            _simulation = new Sim.EquipmentSimulation(new Sim.EquipmentOptions
            {
                EquipmentId = "eq-001",
                Seed = 20260918,
                FaultProfile = Sim.FaultProfile.BearingDegradation,
            });

            _simulation.Connect();
        }

        public int Count => 1;

        public long TicksApplied { get; private set; }

        public void Tick()
        {
            lock (_gate)
            {
                _simulation.WriteSetpoint(100);
                _latest = _simulation.Tick() ?? _latest;
                TicksApplied++;
            }
        }

        public Sim.RawSample? Latest(int equipmentIndex)
        {
            lock (_gate)
            {
                return _latest;
            }
        }

        public Sim.SetpointResult WriteSetpoint(int equipmentIndex, double ratePct)
        {
            lock (_gate)
            {
                return _simulation.WriteSetpoint(ratePct);
            }
        }
    }

    private static int FreePort()
    {
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    [SkippableFact]
    public async Task PolledAndSubscribedPathsMeasuredSideBySide()
    {
        Skip.IfNot(Enabled, "Measurement run; use scripts/measure-poll-vs-subscribe.mjs.");

        var duration = Duration;
        var equipment = new LiveEquipment();
        var pki = Path.Combine(Path.GetTempPath(), "mair-pvs-" + Guid.NewGuid().ToString("N"));

        await using var modbus = new Protocols.ModbusServerHost(equipment, readOnlyPort: 0, writePort: 0);
        modbus.Start();

        await using var opcUa = await Protocols.OpcUaServerHost.StartAsync(
            equipment, ["eq-001"], port: FreePort(), pkiRoot: Path.Combine(pki, "server"));

        using var stopping = new CancellationTokenSource();

        // Polled path: Modbus, exactly as LOAD-001 runs it.
        using var modbusClient = new ModbusTelemetryClient("127.0.0.1", modbus.ActualReadOnlyPort);
        var polledNormaliser = new TelemetryNormaliser("eq-001", "edge-gateway@polled");
        var polledSink = new RecordingEgressSink();
        var polledLoop = new EquipmentPollLoop(
            new ModbusTelemetrySource("eq-001", 0, modbusClient, polledNormaliser),
            new BoundedEgressBuffer(),
            polledSink);

        // Subscribed path: OPC UA with the §1.3 parameters.
        await using var opcUaClient = new OpcUaTelemetryClient(opcUa.EndpointUrl, Path.Combine(pki, "gateway"));
        await opcUaClient.ConnectAsync(stopping.Token);
        await using var subscription = await opcUaClient.SubscribeAsync("eq-001", stopping.Token);

        var subscribedNormaliser = new TelemetryNormaliser("eq-001", "edge-gateway@subscribed");
        var subscribedSink = new RecordingEgressSink();

        var polling = polledLoop.RunAsync(stopping.Token);

        // The subscription pushes; this only drains what has already been assembled.
        var draining = Task.Run(async () =>
        {
            while (!stopping.Token.IsCancellationRequested)
            {
                while (subscription.TryDequeue(out var frame) && frame is not null)
                {
                    subscribedSink.Emit(subscribedNormaliser.Normalise(frame));
                }

                await Task.Delay(20, stopping.Token).ConfigureAwait(false);
            }
        }, stopping.Token);

        // The model clock: real 100 ms steps, anchored so it cannot drift.
        var ticker = Task.Run(async () =>
        {
            var next = Stopwatch.GetTimestamp();
            while (!stopping.Token.IsCancellationRequested)
            {
                equipment.Tick();
                opcUa.NodeManager.Refresh("eq-001");
                next += (long)(Stopwatch.Frequency * 0.1);
                var wait = Stopwatch.GetElapsedTime(Stopwatch.GetTimestamp(), next);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait, stopping.Token).ConfigureAwait(false);
                }
            }
        }, stopping.Token);

        try
        {
            await Task.Delay(duration, stopping.Token);
        }
        catch (OperationCanceledException)
        {
            // Nothing cancels this from outside.
        }

        await stopping.CancelAsync();

        try
        {
            await Task.WhenAll(polling, draining, ticker);
        }
        catch (OperationCanceledException)
        {
            // Expected.
        }

        // Drain whatever the subscription assembled after the loop stopped: it is delivered, and
        // discarding it would understate the queued path for no reason.
        while (subscription.TryDequeue(out var late) && late is not null)
        {
            subscribedSink.Emit(subscribedNormaliser.Normalise(late));
        }

        var produced = equipment.TicksApplied;

        // Distinct sequences, not record counts. A polled client that reads the same sample twice
        // has received two records and one sample, and counting records made the first measurement
        // report 99.8 % coverage for a path that was losing nearly a third of the data.
        var polledDistinct = polledSink.Records.Select(r => r.Sequence).Distinct().Count();
        var subscribedDistinct = subscribedSink.Records.Select(r => r.Sequence).Distinct().Count();

        var summary =
            $"duration_seconds={duration.TotalSeconds:F0}\n" +
            $"samples_produced={produced}\n" +
            $"polled_records={polledSink.Records.Count}\n" +
            $"polled_distinct_samples={polledDistinct}\n" +
            $"polled_gaps={polledNormaliser.SequenceGaps}\n" +
            $"polled_coverage_pct={100.0 * polledDistinct / Math.Max(1, produced):F1}\n" +
            $"subscribed_records={subscribedSink.Records.Count}\n" +
            $"subscribed_distinct_samples={subscribedDistinct}\n" +
            $"subscribed_gaps={subscribedNormaliser.SequenceGaps}\n" +
            $"subscribed_coverage_pct={100.0 * subscribedDistinct / Math.Max(1, produced):F1}\n" +
            $"subscription_incomplete_groups_dropped={subscription.IncompleteGroupsDropped}\n" +
            $"polled_read_failures={polledLoop.ReadFailures}\n" +
            $"polled_last_failure={polledLoop.LastFailure ?? "none"}\n";

        var path = Environment.GetEnvironmentVariable("MAIR_LOAD_OUTPUT");
        if (!string.IsNullOrWhiteSpace(path))
        {
            await File.WriteAllTextAsync(path, summary, CancellationToken.None);
        }

        try
        {
            Directory.Delete(pki, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp PKI directory is not worth failing a measurement over.
        }

        // This test measures; it does not judge. The only assertion is that both paths ran at all,
        // because a comparison between two broken paths would be worse than no comparison.
        Assert.True(polledSink.Records.Count > 0, "the polled path produced nothing");
        Assert.True(subscribedSink.Records.Count > 0, "the subscribed path produced nothing");
    }
}
