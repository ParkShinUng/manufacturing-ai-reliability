using System.Net.Sockets;
using NModbus;
using Sim = Mair.EquipmentSimulator;
using Protocols = Mair.EquipmentSimulator.Protocols;

namespace Mair.EdgeGateway.Tests;

/// <summary>
/// Real sockets, real MBAP framing. These close the ADR-0020 spike items that could only be
/// answered by observation: that exception <c>0x03</c> reaches the client <b>on the wire</b> rather
/// than dropping the connection, and that the read-only listener refuses writes with <c>0x01</c>
/// (OD-003). Reading `ModbusSlave.ApplyRequest` suggested both; that is not the same as seeing it.
/// </summary>
public sealed class ModbusOverTheWireTests : IAsyncLifetime
{
    private sealed class FakeEquipment : Protocols.IEquipmentAccess
    {
        private readonly Sim.RawSample?[] _samples = new Sim.RawSample?[2];

        public FakeEquipment()
        {
            _samples[0] = Sample(rate: 100);
            _samples[1] = Sample(rate: 60, torque: null);
        }

        public int Count => _samples.Length;

        public List<double> Writes { get; } = [];

        public Sim.RawSample? Latest(int equipmentIndex) => _samples[equipmentIndex];

        public Sim.SetpointResult WriteSetpoint(int equipmentIndex, double ratePct)
        {
            Writes.Add(ratePct);
            return Sim.SetpointResult.Accepted;
        }

        private static Sim.RawSample Sample(double rate, double? torque = 42) => new()
        {
            EquipmentId = "eq-001",
            Sequence = 0x0102_0304_0506_0708UL,
            SourceEpochMs = 0xDEAD_BEEF,
            State = Sim.EquipmentState.Running,
            Rpm = 1800,
            TorqueNm = torque,
            CurrentA = 12.34,
            VoltageV = 400,
            TemperatureC = 48.6,
            VibrationRms = 2.25,
            OperationRatePct = rate,
            Flags = [],
            RawQuality = Sim.QualityOverall.Good,
        };
    }

    private FakeEquipment _equipment = null!;
    private Protocols.ModbusServerHost _server = null!;

    public Task InitializeAsync()
    {
        _equipment = new FakeEquipment();

        // Ephemeral ports: a test must not fight whatever is already on 5020/5021, and binding a
        // fixed port would make the suite order-dependent.
        _server = new Protocols.ModbusServerHost(_equipment, readOnlyPort: 0, writePort: 0);
        _server.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _server.DisposeAsync();

    private async Task<IModbusMaster> ConnectRawAsync(int port)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port);
        return new ModbusFactory().CreateMaster(tcp);
    }

    // ------------------------------------------------------------ telemetry

    [Fact]
    public async Task TheGatewayReadsAndDecodesARealBlock()
    {
        using var client = new ModbusTelemetryClient("127.0.0.1", _server.ActualReadOnlyPort);
        await client.ConnectAsync();

        var frame = await client.ReadAsync(0);

        Assert.Equal(1800, frame.Rpm, 1);
        Assert.Equal(12.34, frame.CurrentA, 2);
        Assert.Equal(48.6, frame.TemperatureC, 1);
        Assert.Equal(2.25, frame.VibrationRms, 2);
        Assert.Equal(100, frame.OperationRatePct, 1);

        // The values that would break first if word order were wrong.
        Assert.Equal(0x0102_0304_0506_0708UL, frame.Sequence);
        Assert.Equal(0xDEAD_BEEF, frame.SourceEpochMs);
        Assert.Equal(3, frame.EquipmentStateCode); // RUNNING
    }

    [Fact]
    public async Task ADeadSensorArrivesAsAClearValidityBit()
    {
        // Equipment 1 has a null torque reading. Modbus has no null, so the contract carries it in
        // the quality bitmap and the gateway turns a clear bit into null + SENSOR_MISSING.
        using var client = new ModbusTelemetryClient("127.0.0.1", _server.ActualReadOnlyPort);
        await client.ConnectAsync();

        var frame = await client.ReadAsync(1);

        Assert.False(frame.IsValid(Channel.TorqueNm));
        Assert.True(frame.IsValid(Channel.Rpm));

        var telemetry = new TelemetryNormaliser("eq-002", "edge-gateway@test").Normalise(frame);

        Assert.Null(telemetry.TorqueNm);
        Assert.True(telemetry.HasFlag(Channel.TorqueNm, QualityFlag.SensorMissing));
        Assert.Equal(QualityOverall.Uncertain, telemetry.QualityOverall); // torque is advisory
    }

    [Fact]
    public async Task EachEquipmentIsReachableAtItsOwnUnitIdAndBase()
    {
        using var client = new ModbusTelemetryClient("127.0.0.1", _server.ActualReadOnlyPort);
        await client.ConnectAsync();

        Assert.Equal(100, (await client.ReadAsync(0)).OperationRatePct, 1);
        Assert.Equal(60, (await client.ReadAsync(1)).OperationRatePct, 1);
    }

    // ------------------------------------------------------------ OD-003, on the wire

    [Fact]
    public async Task AWriteToTheReadOnlyListenerReturnsIllegalFunction_AndTheConnectionSurvives()
    {
        // The half that could not be proven by reading the library: the exception has to come back
        // as a Modbus response, not as a dropped socket. A dropped socket would look like a comms
        // fault and send the gateway into its reconnect loop.
        using var master = await ConnectRawAsync(_server.ActualReadOnlyPort);

        var ex = await Assert.ThrowsAsync<SlaveException>(
            () => master.WriteMultipleRegistersAsync(1, 0, [0, 600]));

        Assert.Equal(1, ex.SlaveExceptionCode); // 0x01 Illegal Function
        Assert.Empty(_equipment.Writes);

        // Still usable afterwards.
        var registers = await master.ReadInputRegistersAsync(1, 0, 23);
        Assert.Equal(23, registers.Length);
    }

    [Fact]
    public async Task TheWriteListenerAcceptsTheDocumentedWrite()
    {
        using var master = await ConnectRawAsync(_server.ActualWritePort);

        await master.WriteMultipleRegistersAsync(1, 0, [0, 604]); // 60.4 %

        Assert.Equal(60.4, _equipment.Writes.Single());
    }

    [Fact]
    public async Task AnOutOfRangeSetpointReturnsIllegalDataValueOnTheWire_AndWritesNothing()
    {
        // ADR-0020's deciding requirement, observed rather than inferred: rejected with 0x03,
        // never clamped. FluentModbus could not express this at all — its request validator never
        // sees the value.
        using var master = await ConnectRawAsync(_server.ActualWritePort);

        var ex = await Assert.ThrowsAsync<SlaveException>(
            () => master.WriteMultipleRegistersAsync(1, 0, [0, 1001]));

        Assert.Equal(3, ex.SlaveExceptionCode); // 0x03 Illegal Data Value
        Assert.Empty(_equipment.Writes);

        // And the register was not partially mutated: a following valid write still lands.
        await master.WriteMultipleRegistersAsync(1, 0, [0, 1000]);
        Assert.Equal(100.0, _equipment.Writes.Single());
    }

    [Fact]
    public async Task FC06IsRefusedOnTheWire_BecauseItWouldSetADifferentRate()
    {
        // WriteSingleRegister is FC06. It can only carry half of the two-register int32, so
        // honouring it would apply an arbitrary rate the operator never asked for.
        using var master = await ConnectRawAsync(_server.ActualWritePort);

        var ex = await Assert.ThrowsAsync<SlaveException>(
            () => master.WriteSingleRegisterAsync(1, 0, 600));

        Assert.Equal(2, ex.SlaveExceptionCode); // 0x02 Illegal Data Address
        Assert.Empty(_equipment.Writes);
    }

    [Fact]
    public async Task ReconnectNeedsNoProcessRestart()
    {
        // FR-004 / R-01. The client replaces its own session; nothing outside it restarts.
        using var client = new ModbusTelemetryClient("127.0.0.1", _server.ActualReadOnlyPort);
        await client.ConnectAsync();
        Assert.Equal(1800, (await client.ReadAsync(0)).Rpm, 1);

        await client.ReconnectAsync();

        Assert.Equal(1, client.ReconnectCount);
        Assert.Equal(1800, (await client.ReadAsync(0)).Rpm, 1);
    }
}
