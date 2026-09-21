using System.Diagnostics;
using Protocols = Mair.EquipmentSimulator.Protocols;

namespace Mair.EdgeGateway.Tests;

/// <summary>
/// <b>FAIL-OT-001 / AC-002 / R-01</b> — stop the endpoint the gateway reads, restore it, and the same
/// loop object reconnects and resumes with the gap flagged. Real servers are torn down and rebuilt
/// on the same port; nothing is faked (COD-P2-010).
/// <para>
/// The outage is 10 s, not the specification's 30 s. The reconnect bound is set by the backoff, and
/// the backoff reaches its 8 s cap (±20 %) after 7.75 s of failures, so any outage past that point
/// exercises the same worst case: the restore lands somewhere in a capped wait. Thirty seconds would
/// repeat that wait three more times and prove nothing further, at twenty seconds per protocol on
/// every run of the suite.
/// </para>
/// </summary>
public sealed class EndpointOutageTests
{
    private static readonly TimeSpan Outage = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan OfflineBound = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReconnectBound = TimeSpan.FromSeconds(10);

    private static async Task<TimeSpan> WaitForAsync(Func<bool> condition, TimeSpan bound, string what)
    {
        var started = Stopwatch.GetTimestamp();
        while (!condition())
        {
            Assert.True(Stopwatch.GetElapsedTime(started) < bound, $"{what} did not happen within {bound.TotalSeconds} s");
            await Task.Delay(20);
        }

        return Stopwatch.GetElapsedTime(started);
    }

    /// <summary>The assertions FAIL-OT-001 lists, shared so both protocols are held to the same ones.</summary>
    private static async Task AssertOutageAndRecoveryAsync(
        EquipmentPollLoop loop,
        RecordingEgressSink sink,
        Func<Task> stopEndpoint,
        Func<Task> restoreEndpoint)
    {
        await WaitForAsync(() => sink.Records.Count >= 5, TimeSpan.FromSeconds(15), "initial telemetry");
        var reconnectsBefore = loop.ReconnectTotal;

        await stopEndpoint();
        await WaitForAsync(() => !loop.ProtocolConnected, OfflineBound, "going OFFLINE");

        await Task.Delay(Outage);
        var recordsBeforeRestore = sink.Records.Count;
        var lastSequenceBefore = sink.Records[^1].Sequence;

        await restoreEndpoint();
        await WaitForAsync(() => sink.Records.Count > recordsBeforeRestore, ReconnectBound, "telemetry resuming");

        Assert.True(loop.ProtocolConnected);
        Assert.True(loop.ReconnectTotal > reconnectsBefore, $"reconnect_total stayed at {loop.ReconnectTotal}");

        // The model kept stepping through the outage, so the first record after it has skipped
        // sequences and must say so.
        var resumed = sink.Records[recordsBeforeRestore];
        Assert.True(resumed.Sequence > lastSequenceBefore + 1, $"{lastSequenceBefore} -> {resumed.Sequence}");
        Assert.True(resumed.HasFlag(Channel.Event, QualityFlag.SequenceGap), "the outage gap was not flagged");
    }

    [Fact]
    public async Task ModbusReadOnlyListenerOutage_ReconnectsAndFlagsTheGap()
    {
        var equipment = new LiveEquipment(1);
        using var stopping = new CancellationTokenSource();
        var clock = equipment.RunClockAsync(() => null, stopping.Token);

        var server = new Protocols.ModbusServerHost(equipment, readOnlyPort: 0, writePort: 0);
        server.Start();
        var port = server.ActualReadOnlyPort;

        using var client = new ModbusTelemetryClient("127.0.0.1", port);
        var sink = new RecordingEgressSink();
        var loop = new EquipmentPollLoop(
            new ModbusTelemetrySource("eq-001", 0, client, new TelemetryNormaliser("eq-001", "edge-gateway@fail-ot-001")),
            new BoundedEgressBuffer(),
            sink);
        var run = loop.RunAsync(stopping.Token);

        try
        {
            await AssertOutageAndRecoveryAsync(
                loop,
                sink,
                stopEndpoint: async () => await server.DisposeAsync(),
                restoreEndpoint: () =>
                {
                    server = new Protocols.ModbusServerHost(equipment, readOnlyPort: port, writePort: 0);
                    server.Start();
                    return Task.CompletedTask;
                });
        }
        finally
        {
            await stopping.CancelAsync();
            await Task.WhenAll(run, clock);
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task OpcUaEndpointOutage_ResubscribesAndFlagsTheGap()
    {
        var equipment = new LiveEquipment(1);
        var pki = LiveRig.NewPkiRoot("fail-ot");
        var port = LiveRig.FreePort();

        Protocols.OpcUaServerHost? server = await Protocols.OpcUaServerHost.StartAsync(
            equipment, equipment.Ids, port: port, pkiRoot: Path.Combine(pki, "server"));

        using var stopping = new CancellationTokenSource();
        var clock = equipment.RunClockAsync(() => Volatile.Read(ref server), stopping.Token);

        await using var client = new OpcUaTelemetryClient(server.EndpointUrl, Path.Combine(pki, "gateway"));
        await using var source = new OpcUaSubscriptionTelemetrySource(
            "eq-001", client, new TelemetryNormaliser("eq-001", "edge-gateway@fail-ot-001"));
        var sink = new RecordingEgressSink();
        var loop = new EquipmentPollLoop(source, new BoundedEgressBuffer(), sink);
        var run = loop.RunAsync(stopping.Token);

        try
        {
            await AssertOutageAndRecoveryAsync(
                loop,
                sink,
                stopEndpoint: async () =>
                {
                    var stopped = Interlocked.Exchange(ref server, null);
                    await stopped!.DisposeAsync();
                },
                restoreEndpoint: async () =>
                {
                    var restored = await Protocols.OpcUaServerHost.StartAsync(
                        equipment, equipment.Ids, port: port, pkiRoot: Path.Combine(pki, "server"));
                    Volatile.Write(ref server, restored);
                });
        }
        finally
        {
            await stopping.CancelAsync();
            await Task.WhenAll(run, clock);

            if (server is not null)
            {
                await server.DisposeAsync();
            }

            LiveRig.DeleteQuietly(pki);
        }
    }
}

