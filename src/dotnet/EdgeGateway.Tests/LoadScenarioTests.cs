using System.Diagnostics;
using Protocols = Mair.EquipmentSimulator.Protocols;

namespace Mair.EdgeGateway.Tests;

/// <summary>
/// The real gateway path end to end: simulator → Modbus TCP <b>and</b> the OPC UA subscription →
/// gateway poll loops → egress, in <b>real time</b>.
/// <para>
/// <b>LOAD-001</b> is the 30-minute run. It is the half the tick-driven <c>OT-001</c> cannot prove —
/// scheduler jitter, GC pauses, socket stability, loop overruns only exist in wall-clock time — and
/// it is skipped unless <c>MAIR_LOAD_TEST=1</c>, because it is not a unit test. Invoke it through
/// <c>scripts/run-load-001.mjs</c>, which captures the environment and writes the report (NFR-005,
/// AC-042).
/// </para>
/// <para>
/// <b>AC-001 on the shipping path</b> runs the same rig for a few seconds in the ordinary suite, so
/// "the Edge Gateway emits" is checked through the real servers, clients and loops on every run and
/// not only by the in-process <c>SustainedEmissionTests</c> (COD-P2-006).
/// </para>
/// </summary>
public sealed class LoadScenarioTests
{
    private const int EquipmentCount = 20;

    /// <summary>
    /// Margin after the last loop's first record before the OPC UA zero-gap claim applies. Warm-up is
    /// measured, not assumed: twenty sessions and subscriptions take a variable time to come up, and
    /// a fixed allowance would either hide real gaps or count start-up as loss.
    /// </summary>
    private static readonly TimeSpan WarmUpMargin = TimeSpan.FromSeconds(1);

    private static bool Enabled => Environment.GetEnvironmentVariable("MAIR_LOAD_TEST") == "1";

    private static TimeSpan LoadDuration =>
        int.TryParse(Environment.GetEnvironmentVariable("MAIR_LOAD_SECONDS"), out var s) && s > 0
            ? TimeSpan.FromSeconds(s)
            : TimeSpan.FromMinutes(30);

    private sealed record ProtocolRun(
        string Name,
        IReadOnlyList<EquipmentPollLoop> Loops,
        IReadOnlyList<RecordingEgressSink> Sinks,
        IReadOnlyList<TelemetryNormaliser> Normalisers,
        IReadOnlyList<BoundedEgressBuffer> Buffers)
    {
        public int Records => Sinks.Sum(s => s.Records.Count);

        public int Distinct => Sinks.Sum(s => s.Records.Select(r => r.Sequence).Distinct().Count());

        public long Gaps => Normalisers.Sum(n => n.SequenceGaps);

        public IReadOnlyList<OpcUaSubscriptionTelemetrySource> OpcUaSources { get; init; } = [];

        /// <summary>The report, taken while the subscriptions still exist.</summary>
        public string Snapshot { get; init; } = "";

        /// <summary>How many sequences each flagged gap skipped: "1:30 2:4" means thirty single misses and four doubles.</summary>
        private string GapSizes()
        {
            var sizes = new SortedDictionary<ulong, int>();
            foreach (var sink in Sinks)
            {
                for (var i = 1; i < sink.Records.Count; i++)
                {
                    var (a, b) = (sink.Records[i - 1].Sequence, sink.Records[i].Sequence);
                    if (b > a + 1)
                    {
                        sizes[b - a - 1] = sizes.GetValueOrDefault(b - a - 1) + 1;
                    }
                }
            }

            return sizes.Count == 0 ? "none" : string.Join(" ", sizes.Select(kv => $"{kv.Key}:{kv.Value}"));
        }

        public string Report(long ticks) =>
            $"{Name}_records={Records}\n" +
            $"{Name}_distinct_sequences={Distinct}\n" +
            $"{Name}_duplicates={Records - Distinct}\n" +
            $"{Name}_coverage_pct={100.0 * Distinct / Math.Max(1, ticks * Loops.Count):F1}\n" +
            $"{Name}_sequence_gaps={Gaps}\n" +
            $"{Name}_read_failures={Loops.Sum(l => l.ReadFailures)}\n" +
            $"{Name}_connect_failures={Loops.Sum(l => l.ConnectFailures)}\n" +
            $"{Name}_loop_overruns={Loops.Sum(l => l.LoopOverruns)}\n" +
            $"{Name}_buffer_drops={Buffers.Sum(b => b.DroppedTotal)}\n" +
            $"{Name}_egress_failures={Loops.Sum(l => l.EgressFailures)}\n" +
            $"{Name}_gap_sizes={GapSizes()}\n" +
            (OpcUaSources.Count == 0 ? "" :
                $"{Name}_incomplete_groups_dropped={OpcUaSources.Sum(s => s.Subscription?.IncompleteGroupsDropped ?? 0)}\n" +
                $"{Name}_late_values_discarded={OpcUaSources.Sum(s => s.Subscription?.LateValuesDiscarded ?? 0)}\n") +
            $"{Name}_last_failure={Loops.Select(l => l.LastFailure).FirstOrDefault(f => f is not null) ?? "none"}\n";
    }

    private sealed record RunResult(
        ProtocolRun Run, long Ticks, TimeSpan Elapsed, long Bursts, TimeSpan LongestStepGap, IReadOnlyList<DateTimeOffset> BurstTimes)
    {
        /// <summary>
        /// Whether the gaps line up with the model clock's bursts. A gap within 3 s after a burst is
        /// consistent with §1.3's "steps closer than the sampling interval are not all sampled"; a gap
        /// with no burst near it is not, and would be the subscription losing data.
        /// </summary>
        public string Clock(string name)
        {
            var gapTimes = Run.Sinks
                .SelectMany(s => s.Records)
                .Where(r => r.HasFlag(Channel.Event, QualityFlag.SequenceGap))
                .Select(r => r.IngestTimeUtc)
                .ToList();

            var nearBurst = gapTimes.Count(g => BurstTimes.Any(b => b <= g && g - b < TimeSpan.FromSeconds(3)));
            var start = gapTimes.Count > 0 ? gapTimes.Min() : DateTimeOffset.UtcNow;

            return
                $"{name}_model_clock_bursts={Bursts}\n" +
                $"{name}_longest_step_gap_ms={LongestStepGap.TotalMilliseconds:F0}\n" +
                $"{name}_gaps_within_3s_after_a_burst={nearBurst}/{gapTimes.Count}\n" +
                $"{name}_first_gap_times_utc={string.Join(" ", gapTimes.Order().Take(8).Select(t => t.ToString("HH:mm:ss.fff")))}\n" +
                $"{name}_first_burst_times_utc={string.Join(" ", BurstTimes.Take(8).Select(t => t.ToString("HH:mm:ss.fff")))}\n";
        }

        public DateTimeOffset WarmedUpAt =>
            Run.Sinks.Max(s => s.Records.Count > 0 ? s.Records[0].IngestTimeUtc : DateTimeOffset.MaxValue) + WarmUpMargin;
    }

    private enum Path2 { Modbus, OpcUa }

    /// <summary>
    /// One protocol path for all twenty machines. One protocol at a time, because that is what a
    /// gateway does - each machine is read over one protocol - and running both in one process
    /// measured the test rig's contention rather than either path: forty loops and twenty sessions
    /// halved the achieved poll rate on a laptop.
    /// </summary>
    private static async Task<RunResult> RunAsync(Path2 path, TimeSpan duration)
    {
        var equipment = new LiveEquipment(EquipmentCount);
        var pki = LiveRig.NewPkiRoot("load");

        await using var modbusServer = new Protocols.ModbusServerHost(equipment, readOnlyPort: 0, writePort: 0);
        modbusServer.Start();

        await using var opcUaServer = await Protocols.OpcUaServerHost.StartAsync(
            equipment, equipment.Ids, port: LiveRig.FreePort(), pkiRoot: Path.Combine(pki, "server"));

        using var stopping = new CancellationTokenSource();
        var clock = equipment.RunClockAsync(() => opcUaServer, stopping.Token);

        var modbusClients = new List<ModbusTelemetryClient>();
        var opcUaClients = new List<OpcUaTelemetryClient>();
        var opcUaSources = new List<OpcUaSubscriptionTelemetrySource>();

        ProtocolRun Build(string name, Func<int, TelemetryNormaliser, ITelemetrySource> source)
        {
            var loops = new List<EquipmentPollLoop>();
            var sinks = new List<RecordingEgressSink>();
            var normalisers = new List<TelemetryNormaliser>();
            var buffers = new List<BoundedEgressBuffer>();

            for (var i = 0; i < EquipmentCount; i++)
            {
                var normaliser = new TelemetryNormaliser(LiveEquipment.IdOf(i), $"edge-gateway@{name}");
                var buffer = new BoundedEgressBuffer(onDrop: normaliser.NoteBufferOverflowDrop);
                var sink = new RecordingEgressSink();

                loops.Add(new EquipmentPollLoop(source(i, normaliser), buffer, sink));
                sinks.Add(sink);
                normalisers.Add(normaliser);
                buffers.Add(buffer);
            }

            return new ProtocolRun(name, loops, sinks, normalisers, buffers);
        }

        var run = path == Path2.Modbus
            ? Build("modbus", (i, normaliser) =>
            {
                var client = new ModbusTelemetryClient("127.0.0.1", modbusServer.ActualReadOnlyPort);
                modbusClients.Add(client);
                return new ModbusTelemetrySource(LiveEquipment.IdOf(i), i, client, normaliser);
            })
            : Build("opcua", (i, normaliser) =>
            {
                var client = new OpcUaTelemetryClient(opcUaServer.EndpointUrl, Path.Combine(pki, "gateway"));
                var source = new OpcUaSubscriptionTelemetrySource(LiveEquipment.IdOf(i), client, normaliser);
                opcUaClients.Add(client);
                opcUaSources.Add(source);
                return source;
            });

        var started = Stopwatch.GetTimestamp();
        var running = run.Loops.Select(l => l.RunAsync(stopping.Token)).ToList();

        await Task.Delay(duration);

        var elapsed = Stopwatch.GetElapsedTime(started);
        var ticks = equipment.TicksApplied;
        await stopping.CancelAsync();
        await Task.WhenAll(running.Append(clock));

        // Read before the sources are disposed below, which drops their subscriptions.
        run = run with { OpcUaSources = opcUaSources };
        var report = run.Report(ticks);

        foreach (var source in opcUaSources)
        {
            await source.DisposeAsync();
        }

        foreach (var client in opcUaClients)
        {
            await client.DisposeAsync();
        }

        foreach (var client in modbusClients)
        {
            client.Dispose();
        }

        LiveRig.DeleteQuietly(pki);
        return new RunResult(
            run with { Snapshot = report }, ticks, elapsed, equipment.Bursts, equipment.LongestStepGap, equipment.BurstTimes);
    }

    /// <summary>
    /// Modbus, OD-005: detection, not prevention. Every skipped sequence must carry
    /// <c>SEQUENCE_GAP</c> — the claim is that no loss is silent, not that there is none.
    /// </summary>
    private static void AssertEverySkipIsFlagged(ProtocolRun run)
    {
        foreach (var sink in run.Sinks)
        {
            for (var i = 1; i < sink.Records.Count; i++)
            {
                var (previous, current) = (sink.Records[i - 1], sink.Records[i]);
                if (current.Sequence > previous.Sequence + 1)
                {
                    Assert.True(current.HasFlag(Channel.Event, QualityFlag.SequenceGap),
                        $"{current.EquipmentId}: {previous.Sequence} -> {current.Sequence} was not flagged");
                }
            }
        }
    }

    /// <summary>OPC UA subscription, OD-005: prevention. Zero gaps once the subscriptions are up.</summary>
    private static void AssertNoGapsAfterWarmUp(ProtocolRun run, DateTimeOffset warmedUpAt)
    {
        var gapsAfterWarmUp = run.Sinks
            .SelectMany(s => s.Records)
            .Where(r => r.IngestTimeUtc > warmedUpAt)
            .Count(r => r.HasFlag(Channel.Event, QualityFlag.SequenceGap));

        Assert.Equal(0, gapsAfterWarmUp);
        Assert.Equal(0, run.Buffers.Sum(b => b.DroppedTotal));
    }

    private static void AssertEmitsCanonical(ProtocolRun run, SourceProtocol protocol)
    {
        // Every machine got telemetry out of the gateway, and every record is canonical.
        // Says why a machine produced nothing, not only that it did not: an empty sink with a
        // connected loop and one with a loop stuck reconnecting are different faults. Added while
        // chasing the intermittent recorded in TEST_SPECIFICATIONS.md, whose first sighting left no
        // cause behind.
        var silent = run.Sinks.Select((s, i) => (Sink: s, Loop: run.Loops[i], Index: i)).Where(x => x.Sink.Records.Count == 0).ToList();
        Assert.True(silent.Count == 0,
            $"{silent.Count} of {run.Sinks.Count} machines emitted nothing. " + string.Join(" | ", silent.Take(5).Select(x =>
                $"#{x.Index}: connected={x.Loop.ProtocolConnected} reconnects={x.Loop.ReconnectTotal} readFailures={x.Loop.ReadFailures} " +
                $"connectFailures={x.Loop.ConnectFailures} polls={x.Loop.PollsCompleted} last={x.Loop.LastFailure ?? "none"}")));

        foreach (var record in run.Sinks.SelectMany(s => s.Records))
        {
            LiveRig.AssertCanonical(record);
            Assert.Equal(protocol, record.SourceProtocol);
        }

        Assert.All(run.Loops, l => Assert.True(l.ProtocolConnected, $"{run.Name}: {l.LastFailure}"));
        Assert.Equal(0, run.Loops.Sum(l => l.EgressFailures));
    }

    [Fact]
    public async Task Ac001_TwentyMachinesEmitCanonicalTelemetryOverModbus()
    {
        var result = await RunAsync(Path2.Modbus, TimeSpan.FromSeconds(5));

        AssertEmitsCanonical(result.Run, SourceProtocol.ModbusTcp);
        AssertEverySkipIsFlagged(result.Run);
    }

    [Fact]
    public async Task Ac001_TwentyMachinesEmitCanonicalTelemetryOverTheOpcUaSubscription()
    {
        var result = await RunAsync(Path2.OpcUa, TimeSpan.FromSeconds(8));

        AssertEmitsCanonical(result.Run, SourceProtocol.OpcUa);
    }

    [SkippableFact]
    public async Task Load001_TwentyEquipmentAtTenHertz()
    {
        Skip.IfNot(Enabled, "LOAD-001 runs only under MAIR_LOAD_TEST=1; use scripts/run-load-001.mjs.");

        // Sequential, each for the full duration: see RunAsync for why not side by side.
        var modbus = await RunAsync(Path2.Modbus, LoadDuration);
        var opcUa = await RunAsync(Path2.OpcUa, LoadDuration);

        // Written where the driver script can pick it up, because a number printed to a test log is
        // not evidence (NFR-005, AC-042).
        var summary =
            $"equipment={EquipmentCount}\n" +
            $"modbus_elapsed_seconds={modbus.Elapsed.TotalSeconds:F1}\n" +
            $"modbus_model_ticks={modbus.Ticks}\n" +
            modbus.Run.Snapshot +
            modbus.Clock("modbus") +
            $"opcua_elapsed_seconds={opcUa.Elapsed.TotalSeconds:F1}\n" +
            $"opcua_model_ticks={opcUa.Ticks}\n" +
            $"opcua_warm_up_seconds={(opcUa.WarmedUpAt - WarmUpMargin - opcUa.Run.Sinks.Min(s => s.Records.Count > 0 ? s.Records[0].IngestTimeUtc : DateTimeOffset.MaxValue)).TotalSeconds:F1}\n" +
            opcUa.Run.Snapshot +
            opcUa.Clock("opcua");

        var path = Environment.GetEnvironmentVariable("MAIR_LOAD_OUTPUT");
        if (!string.IsNullOrWhiteSpace(path))
        {
            await File.WriteAllTextAsync(path, summary);
        }

        // The per-protocol matrix in TEST_SPECIFICATIONS section 5 (OD-005). Coverage is reported,
        // never asserted: it is a measurement, not a threshold.
        AssertEmitsCanonical(modbus.Run, SourceProtocol.ModbusTcp);
        AssertEverySkipIsFlagged(modbus.Run);
        Assert.Equal(0, modbus.Run.Loops.Sum(l => l.ReadFailures));

        AssertEmitsCanonical(opcUa.Run, SourceProtocol.OpcUa);
        AssertNoGapsAfterWarmUp(opcUa.Run, opcUa.WarmedUpAt);
    }

    /// <summary>
    /// Diagnostic, not LOAD-001: the OPC UA half alone, under <c>MAIR_LOAD_DIAGNOSE=opcua</c>. Used to
    /// find where the first LOAD-001 run's 35 OPC UA gaps came from without re-running Modbus.
    /// </summary>
    [SkippableFact]
    public async Task Load001Diagnostic_OpcUaOnly()
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("MAIR_LOAD_DIAGNOSE") == "opcua", "diagnostic only");

        var opcUa = await RunAsync(Path2.OpcUa, LoadDuration);
        var summary = $"opcua_model_ticks={opcUa.Ticks}\n" + opcUa.Run.Snapshot + opcUa.Clock("opcua");

        var path = Environment.GetEnvironmentVariable("MAIR_LOAD_OUTPUT");
        if (!string.IsNullOrWhiteSpace(path))
        {
            await File.WriteAllTextAsync(path, summary);
        }
    }
}
