using Sim = Mair.EquipmentSimulator;
using Protocols = Mair.EquipmentSimulator.Protocols;

namespace Mair.EdgeGateway.Tests;

/// <summary>
/// Closes the loophole Codex flagged when auditing the OD-005 measurement.
/// <para>
/// <see cref="OpcUaSubscription"/> assembles a sample from the ten values that share the
/// <c>SequenceNo</c> value's source timestamp (§1.3). Callback ordering, independent item sampling
/// and uneven queues could all, under a weaker rule, pair a marker from step N with a measurement
/// from N-1. Coverage was measured; <b>atomicity is checked here</b>, over the wire.
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

    /// <summary>
    /// 120 ms is slower than the 100 ms publish, so it rarely queues more than one value per item -
    /// Codex's point: it cannot catch a race it never provokes. 60 ms puts one to two steps in most
    /// cycles; 20 ms is faster than the 50 ms sampling, so the server skips steps and items can
    /// disagree about which ones they saw. Every emitted sample must still be one step's values.
    /// The deterministic cases are in <see cref="OpcUaSampleAssemblerTests"/>.
    /// </summary>
    [Theory]
    [InlineData(120, 40)]
    [InlineData(60, 80)]
    [InlineData(20, 200)]
    [InlineData(0, 200)] // bursts: five steps back to back, then a pause - steps can share a clock reading
    public async Task AnAssembledSampleCarriesOneModelStepsValues(int tickMs, int steps)
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
        for (var i = 0; i < steps; i++)
        {
            equipment.Tick();
            server.NodeManager.Refresh("eq-001");

            if (equipment.Latest(0) is { } sample)
            {
                expected[sample.Sequence] = (sample.SourceEpochMs, sample.VibrationRms);
            }

            await Task.Delay(tickMs > 0 ? tickMs : (i % 5 == 4 ? 150 : 0));
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
