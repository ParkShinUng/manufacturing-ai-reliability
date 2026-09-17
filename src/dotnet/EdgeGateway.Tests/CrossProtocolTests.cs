using Sim = Mair.EquipmentSimulator;
using Protocols = Mair.EquipmentSimulator.Protocols;

namespace Mair.EdgeGateway.Tests;

/// <summary>
/// <b>OT-002 / AC-021</b> — OPC UA and Modbus clients reading the same equipment produce
/// <b>identical canonical engineering values</b>.
/// <para>
/// This is the test that catches scaling and word-order errors, and it works because the two sides
/// are independent implementations: the simulator's Modbus encoder and the gateway's decoder were
/// written separately, and OPC UA carries typed values that bypass the register map entirely. A
/// disagreement means one of them is wrong; a shared codec would have made this pass by
/// construction and proven nothing.
/// </para>
/// </summary>
public sealed class CrossProtocolTests : IAsyncLifetime
{
    private sealed class FakeEquipment : Protocols.IEquipmentAccess
    {
        private Sim.RawSample?[] _samples = [Sample()];

        public int Count => _samples.Length;

        public Sim.RawSample? Latest(int equipmentIndex) => _samples[equipmentIndex];

        public Sim.SetpointResult WriteSetpoint(int equipmentIndex, double ratePct)
            => Sim.SetpointResult.Accepted;

        public void Set(Sim.RawSample sample) => _samples = [sample];

        public static Sim.RawSample Sample(
            double? rpm = 1234.5, double? torque = -7.3, double? current = 18.62, double? voltage = 398.7,
            double? temperature = 133.3, double? vibration = 28.61, double? rate = 60.4,
            ulong sequence = 0x0102_0304_0506_0708UL, uint epochMs = 0xDEAD_BEEF,
            Sim.EquipmentState state = Sim.EquipmentState.Degraded) => new()
        {
            EquipmentId = "eq-001",
            Sequence = sequence,
            SourceEpochMs = epochMs,
            State = state,
            Rpm = rpm,
            TorqueNm = torque,
            CurrentA = current,
            VoltageV = voltage,
            TemperatureC = temperature,
            VibrationRms = vibration,
            OperationRatePct = rate,
            Flags = [],
            RawQuality = Sim.QualityOverall.Good,
        };
    }

    private static readonly Channel[] Measurements =
    [
        Channel.Rpm, Channel.TorqueNm, Channel.CurrentA, Channel.VoltageV,
        Channel.TemperatureC, Channel.VibrationRms, Channel.OperationRatePct,
    ];

    /// <summary>Documented resolution per channel (`EQUIPMENT_MODEL_AND_STATE.md` §1.2).</summary>
    private static double ResolutionOf(Channel channel) => channel switch
    {
        Channel.CurrentA or Channel.VibrationRms => 0.01,
        _ => 0.1,
    };

    private FakeEquipment _equipment = null!;
    private Protocols.ModbusServerHost _modbusServer = null!;
    private Protocols.OpcUaServerHost _opcUaServer = null!;
    private string _pki = null!;

    public async Task InitializeAsync()
    {
        _equipment = new FakeEquipment();
        _pki = Path.Combine(Path.GetTempPath(), "mair-xproto-" + Guid.NewGuid().ToString("N"));

        _modbusServer = new Protocols.ModbusServerHost(_equipment, readOnlyPort: 0, writePort: 0);
        _modbusServer.Start();

        _opcUaServer = await Protocols.OpcUaServerHost.StartAsync(
            _equipment, ["eq-001"], port: FreePort(), pkiRoot: Path.Combine(_pki, "server"));
    }

    public async Task DisposeAsync()
    {
        await _opcUaServer.DisposeAsync();
        await _modbusServer.DisposeAsync();

        try
        {
            Directory.Delete(_pki, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp PKI directory is not worth failing a test over.
        }
    }

    private static int FreePort()
    {
        // OPC UA base addresses are resolved from the configured URL, so port 0 cannot be used the
        // way it can with a raw TcpListener. Borrow a free one and release it immediately.
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private async Task<(CanonicalTelemetry Modbus, CanonicalTelemetry OpcUa)> ReadBothAsync()
    {
        _opcUaServer.NodeManager.RefreshAll();

        using var modbusClient = new ModbusTelemetryClient("127.0.0.1", _modbusServer.ActualReadOnlyPort);
        await modbusClient.ConnectAsync();
        var modbusFrame = await modbusClient.ReadAsync(0);

        await using var opcUaClient = new OpcUaTelemetryClient(
            _opcUaServer.EndpointUrl, Path.Combine(_pki, "gateway"));
        await opcUaClient.ConnectAsync();
        var opcUaFrame = await opcUaClient.ReadAsync("eq-001");

        var modbus = new TelemetryNormaliser("eq-001", "edge-gateway@modbus").Normalise(modbusFrame);
        var opcUa = new TelemetryNormaliser("eq-001", "edge-gateway@opcua").Normalise(
            opcUaFrame.Values, opcUaFrame.Sequence, opcUaFrame.SourceEpochMs,
            opcUaFrame.State, opcUaFrame.SourceTimeUtc);

        return (modbus, opcUa);
    }

    [Fact]
    public async Task BothProtocolsProduceIdenticalEngineeringValues()
    {
        var (modbus, opcUa) = await ReadBothAsync();

        foreach (var channel in Measurements)
        {
            Assert.NotNull(modbus[channel]);
            Assert.NotNull(opcUa[channel]);

            // Within the channel's documented resolution. The Modbus path is quantised by its
            // scale factor; OPC UA carries a Double. Agreement to the resolution is the contract.
            Assert.True(Math.Abs(modbus[channel]!.Value - opcUa[channel]!.Value) <= ResolutionOf(channel),
                $"{channel}: Modbus {modbus[channel]} vs OPC UA {opcUa[channel]}");
        }
    }

    [Fact]
    public async Task BothProtocolsAgreeOnSequenceEpochAndState()
    {
        // The values a word-order error breaks first. The Modbus path reassembles these from
        // registers; OPC UA reads them as typed scalars, so the two disagree the moment the
        // register map is misread.
        var (modbus, opcUa) = await ReadBothAsync();

        Assert.Equal(0x0102_0304_0506_0708UL, modbus.Sequence);
        Assert.Equal(modbus.Sequence, opcUa.Sequence);
        Assert.Equal(EquipmentState.Degraded, modbus.EquipmentState);
        Assert.Equal(modbus.EquipmentState, opcUa.EquipmentState);
    }

    [Fact]
    public async Task ANegativeChannelSurvivesBothPaths()
    {
        // torqueNm is the one channel whose range goes below zero. An unsigned register decode
        // turns -7.3 into roughly 429 million, and only the second protocol notices.
        var (modbus, opcUa) = await ReadBothAsync();

        Assert.True(modbus.TorqueNm < 0, $"Modbus torque was {modbus.TorqueNm}");
        Assert.True(opcUa.TorqueNm < 0, $"OPC UA torque was {opcUa.TorqueNm}");
        Assert.Equal(opcUa.TorqueNm!.Value, modbus.TorqueNm!.Value, 1);
    }

    [Fact]
    public async Task ADeadSensorIsNullOnBothPaths_WithTheSameDerivedQuality()
    {
        // Modbus carries it as a cleared validity bit, OPC UA as Bad_OutOfService. Two entirely
        // different mechanisms that must arrive at the same canonical event.
        _equipment.Set(FakeEquipment.Sample(vibration: null));

        var (modbus, opcUa) = await ReadBothAsync();

        Assert.Null(modbus.VibrationRms);
        Assert.Null(opcUa.VibrationRms);
        Assert.True(modbus.HasFlag(Channel.VibrationRms, QualityFlag.SensorMissing));
        Assert.True(opcUa.HasFlag(Channel.VibrationRms, QualityFlag.SensorMissing));

        // vibrationRms is safety-required, so both must be BAD, not merely both non-GOOD.
        Assert.Equal(QualityOverall.Bad, modbus.QualityOverall);
        Assert.Equal(QualityOverall.Bad, opcUa.QualityOverall);
    }

    [Fact]
    public async Task OnlyModbusSynthesisesItsTimestamp()
    {
        // The one flag that must legitimately differ: Modbus has no source timestamp and always
        // raises TIMESTAMP_SYNTHESISED (§2.6); OPC UA carries a real one. This is asserted so the
        // difference stays deliberate rather than becoming an unexplained divergence later.
        var (modbus, opcUa) = await ReadBothAsync();

        Assert.True(modbus.HasFlag(Channel.Event, QualityFlag.TimestampSynthesised));
        Assert.False(opcUa.HasFlag(Channel.Event, QualityFlag.TimestampSynthesised));

        Assert.Equal(SourceProtocol.ModbusTcp, modbus.SourceProtocol);
        Assert.Equal(SourceProtocol.OpcUa, opcUa.SourceProtocol);

        // ...and it changes neither derived quality, or every Modbus machine would be UNCERTAIN.
        Assert.Equal(modbus.QualityOverall, opcUa.QualityOverall);
    }

    [Fact]
    public async Task TheNamespaceIndexIsResolvedByUri_NotAssumed()
    {
        // Hard-coding index 2 is the single most common OPC UA integration defect, and §1.1 forbids
        // it explicitly. Proving the client resolved *something* by URI is the closest a test can
        // get to proving it did not guess.
        await using var client = new OpcUaTelemetryClient(
            _opcUaServer.EndpointUrl, Path.Combine(_pki, "gateway-ns"));
        await client.ConnectAsync();

        Assert.True(client.NamespaceIndex > 0, "namespace 0 is the OPC UA core namespace");
        Assert.True(client.IsConnected);
    }
}
