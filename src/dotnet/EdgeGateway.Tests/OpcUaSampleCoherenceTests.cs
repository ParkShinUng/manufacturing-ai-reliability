using Sim = Mair.EquipmentSimulator;
using Protocols = Mair.EquipmentSimulator.Protocols;

namespace Mair.EdgeGateway.Tests;

/// <summary>
/// Closes the loophole Codex flagged when auditing the OD-005 measurement.
/// <para>
/// <see cref="OpcUaSubscription"/> assembles a sample when <c>SequenceNo</c> reports, using the
/// latest known value of every other node. That is right for a node that does not change — a
/// monitored item reports on change, so <c>State</c> would otherwise never complete a sample — but
/// it means callback ordering could in principle pair a marker from tick N with a measurement from
/// N-1. Coverage was measured; <b>atomicity was not</b>.
/// </para>
/// <para>
/// A sample stitched from two model steps is not a reading the equipment ever produced, which puts
/// it in the same category as the substituted values ADR-0018 forbids. So it is checked rather than
/// argued about.
/// </para>
/// </summary>
public sealed class OpcUaSampleCoherenceTests
{
    private sealed class TickingEquipment : Protocols.IEquipmentAccess
    {
        private readonly Sim.EquipmentSimulation _simulation;
        private Sim.RawSample? _latest;
        private readonly Lock _gate = new();

        public TickingEquipment()
        {
            _simulation = new Sim.EquipmentSimulation(new Sim.EquipmentOptions
            {
                EquipmentId = "eq-001",
                Seed = 20260921,
                FaultProfile = Sim.FaultProfile.BearingDegradation,
            });

            _simulation.Connect();
        }

        public int Count => 1;

        public void Tick()
        {
            lock (_gate)
            {
                _simulation.WriteSetpoint(100);
                _latest = _simulation.Tick() ?? _latest;
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

    [Fact]
    public async Task AnAssembledSampleCarriesOneModelStepsValues()
    {
        var equipment = new TickingEquipment();
        var pki = Path.Combine(Path.GetTempPath(), "mair-coh-" + Guid.NewGuid().ToString("N"));

        await using var server = await Protocols.OpcUaServerHost.StartAsync(
            equipment, ["eq-001"], port: FreePort(), pkiRoot: Path.Combine(pki, "server"));

        await using var client = new OpcUaTelemetryClient(server.EndpointUrl, Path.Combine(pki, "gateway"));
        await client.ConnectAsync();
        await using var subscription = await client.SubscribeAsync("eq-001");

        // Drive a spread of model steps. The vibration channel changes on every tick under bearing
        // degradation, so a stitched sample would show a value that belongs to a different step.
        var expected = new Dictionary<ulong, (uint Epoch, double? Vibration)>();
        for (var i = 0; i < 40; i++)
        {
            equipment.Tick();
            server.NodeManager.Refresh("eq-001");

            if (equipment.Latest(0) is { } sample)
            {
                expected[sample.Sequence] = (sample.SourceEpochMs, sample.VibrationRms);
            }

            await Task.Delay(120);
        }

        await Task.Delay(500); // let the last publishing cycle arrive

        var checkedSamples = 0;
        while (subscription.TryDequeue(out var frame) && frame is not null)
        {
            if (!expected.TryGetValue(frame.Sequence, out var truth))
            {
                continue; // a sample from before the recording started
            }

            // The epoch is the decisive one: it is assigned by the equipment in the same model step
            // as the sequence, so a mismatch means the two came from different steps.
            Assert.Equal(truth.Epoch, frame.SourceEpochMs);

            if (truth.Vibration is { } v && frame.Values[Channel.VibrationRms] is { } got)
            {
                Assert.Equal(v, got, 2);
            }

            checkedSamples++;
        }

        Assert.True(checkedSamples >= 10, $"only {checkedSamples} samples were available to check");

        try
        {
            Directory.Delete(pki, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp PKI directory is not worth failing a test over.
        }
    }
}
