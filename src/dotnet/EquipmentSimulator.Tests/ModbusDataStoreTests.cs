using Mair.EquipmentSimulator.Protocols;
using NModbus;

namespace Mair.EquipmentSimulator.Tests;

/// <summary>
/// The Modbus server side of <c>OT_PROTOCOL_MAPPING.md</c> §2, and the ADR-0020 spike items:
/// an out-of-range setpoint must be refused with exception <c>0x03</c> rather than clamped, and the
/// read-only listener must refuse every write with <c>0x01</c> (OD-003).
/// </summary>
public sealed class ModbusDataStoreTests
{
    private sealed class FakeEquipment : IEquipmentAccess
    {
        private readonly RawSample?[] _samples;

        public FakeEquipment(int count = 3)
        {
            _samples = new RawSample?[count];
            for (var i = 0; i < count; i++)
            {
                _samples[i] = Sample($"eq-{i + 1:D3}");
            }
        }

        public int Count => _samples.Length;

        public List<(int Index, double Rate)> Writes { get; } = [];

        public SetpointResult NextResult { get; set; } = SetpointResult.Accepted;

        public void Silence(int index) => _samples[index] = null;

        public RawSample? Latest(int equipmentIndex) => _samples[equipmentIndex];

        public SetpointResult WriteSetpoint(int equipmentIndex, double ratePct)
        {
            if (NextResult == SetpointResult.Accepted)
            {
                Writes.Add((equipmentIndex, ratePct));
            }

            return NextResult;
        }

        private static RawSample Sample(string id) => new()
        {
            EquipmentId = id,
            Sequence = 42,
            SourceEpochMs = 4_200,
            State = EquipmentState.Running,
            Rpm = 1800,
            TorqueNm = 42,
            CurrentA = 12,
            VoltageV = 400,
            TemperatureC = 48,
            VibrationRms = 2.2,
            OperationRatePct = 100,
            Flags = [],
            RawQuality = QualityOverall.Good,
        };
    }

    /// <summary>
    /// The Modbus exception code the slave would put on the wire. Asserting the code rather than
    /// merely "it threw" is the point: <c>0x02</c> and <c>0x03</c> mean different things to the
    /// Control Service, and only one of them says "your value was wrong".
    /// </summary>
    private static byte CodeOf(Action act)
        => Assert.Throws<InvalidModbusRequestException>(act).ExceptionCode;

    private static ushort[] Setpoint(int rawScaled) =>
        [(ushort)((uint)rawScaled >> 16), (ushort)((uint)rawScaled & 0xFFFF)];

    // ------------------------------------------------------------ telemetry reads

    [Fact]
    public void AReadReturnsTheTwentyThreeRegisterBlock()
    {
        var store = new ModbusTelemetryDataStore(new FakeEquipment(), writable: false);

        var block = store.InputRegisters.ReadPoints(0, 23);

        Assert.Equal(23, block.Length);
        Assert.Equal(42UL, ((ulong)block[16] << 48) | ((ulong)block[17] << 32) | ((ulong)block[18] << 16) | block[19]);
    }

    [Fact]
    public void EachEquipmentIsAtItsOwnBase()
    {
        var store = new ModbusTelemetryDataStore(new FakeEquipment(), writable: false);

        // Without a per-equipment base every machine would alias the same registers.
        for (ushort i = 0; i < 3; i++)
        {
            _ = store.InputRegisters.ReadPoints((ushort)(40 * i), 23);
        }

        Assert.Equal(2, CodeOf(() => store.InputRegisters.ReadPoints(40 * 3, 23))); // past the last
    }

    [Fact]
    public void AMisalignedOrWrongLengthReadIsRefused_NotServedPartially()
    {
        // A read starting mid-block or spanning two equipment would return a physically meaningless
        // mixture. Serving it would look like working telemetry.
        var store = new ModbusTelemetryDataStore(new FakeEquipment(), writable: false);

        Assert.Equal(2, CodeOf(() => store.InputRegisters.ReadPoints(1, 23)));
        Assert.Equal(2, CodeOf(() => store.InputRegisters.ReadPoints(0, 22)));
        Assert.Equal(2, CodeOf(() => store.InputRegisters.ReadPoints(0, 24)));
    }

    [Fact]
    public void SilentEquipmentFailsTheRead_RatherThanReturningStaleRegisters()
    {
        var equipment = new FakeEquipment();
        var store = new ModbusTelemetryDataStore(equipment, writable: false);
        equipment.Silence(0);

        Assert.Equal(4, CodeOf(() => store.InputRegisters.ReadPoints(0, 23))); // SlaveDeviceFailure
    }

    // ------------------------------------------------------------ OD-003

    [Fact]
    public void TheReadOnlyListenerRefusesEveryWriteWithIllegalFunction()
    {
        // This is the whole of OD-003. Modbus TCP has no identity, so the guarantee is that this
        // listener has no write code path — not that the gateway promises not to write.
        var store = new ModbusTelemetryDataStore(new FakeEquipment(), writable: false);

        Assert.Equal(1, CodeOf(() => store.HoldingRegisters.WritePoints(0, Setpoint(600))));
        Assert.Equal(1, CodeOf(() => store.HoldingRegisters.WritePoints(0, [600])));
        Assert.Equal(1, CodeOf(() => store.HoldingRegisters.WritePoints(4, Setpoint(1000))));
    }

    [Fact]
    public void TheReadOnlyListenerStillServesTelemetry()
    {
        // Refusing writes must not make it useless — it is the gateway's only source.
        var store = new ModbusTelemetryDataStore(new FakeEquipment(), writable: false);

        Assert.Equal(23, store.InputRegisters.ReadPoints(0, 23).Length);
    }

    // ------------------------------------------------------------ the single write

    [Fact]
    public void AValidWriteIsAccepted()
    {
        var equipment = new FakeEquipment();
        var store = new ModbusTelemetryDataStore(equipment, writable: true);

        store.HoldingRegisters.WritePoints(4, Setpoint(604)); // equipment index 1, 60.4 %

        Assert.Equal((1, 60.4), equipment.Writes.Single());
    }

    [Fact]
    public void FC06IsRefused_BecauseItCanOnlyWriteHalfTheSetpoint()
    {
        // operationRateSetpointPct is a two-register int32. A single-register write is not a
        // malformed request that gets rejected downstream — it is a DIFFERENT rate the equipment
        // would act on. This is the defect the ADR-0020 challenge found in the contract itself.
        var equipment = new FakeEquipment();
        var store = new ModbusTelemetryDataStore(equipment, writable: true);

        Assert.Equal(2, CodeOf(() => store.HoldingRegisters.WritePoints(0, [600])));
        Assert.Empty(equipment.Writes);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void AWrongQuantityIsRefused(int quantity)
    {
        var equipment = new FakeEquipment();
        var store = new ModbusTelemetryDataStore(equipment, writable: true);

        Assert.Equal(2, CodeOf(() => store.HoldingRegisters.WritePoints(0, new ushort[quantity])));
        Assert.Empty(equipment.Writes);
    }

    [Fact]
    public void AWriteNotStartingAtTheBaseIsRefused()
    {
        var equipment = new FakeEquipment();
        var store = new ModbusTelemetryDataStore(equipment, writable: true);

        Assert.Equal(2, CodeOf(() => store.HoldingRegisters.WritePoints(1, Setpoint(600))));
        Assert.Equal(2, CodeOf(() => store.HoldingRegisters.WritePoints(2, Setpoint(600))));
        Assert.Empty(equipment.Writes);
    }

    [Theory]
    [InlineData(1001)]
    [InlineData(5000)]
    [InlineData(-1)]
    [InlineData(int.MinValue)]
    public void AnOutOfRangeSetpointIsRefusedWithIllegalDataValue_AndNothingIsWritten(int rawScaled)
    {
        // ADR-0020's deciding requirement: rejected with 0x03, never clamped. FluentModbus cannot
        // express this — its request validator never sees the value.
        var equipment = new FakeEquipment();
        var store = new ModbusTelemetryDataStore(equipment, writable: true);

        Assert.Equal(3, CodeOf(() => store.HoldingRegisters.WritePoints(0, Setpoint(rawScaled))));
        Assert.Empty(equipment.Writes);
    }

    [Theory]
    [InlineData(0, 0.0)]
    [InlineData(1000, 100.0)]
    public void TheRangeBoundariesAreAccepted(int rawScaled, double expected)
    {
        // Off-by-one at the boundary would reject a legitimate full-rate command.
        var equipment = new FakeEquipment();
        var store = new ModbusTelemetryDataStore(equipment, writable: true);

        store.HoldingRegisters.WritePoints(0, Setpoint(rawScaled));

        Assert.Equal(expected, equipment.Writes.Single().Rate);
    }

    [Fact]
    public void AStateRefusalFromTheEquipmentIsReported_NotSwallowed()
    {
        // The value was in range, so the equipment refused on state — FAULT, STOPPING, OFFLINE.
        // Returning success would tell the Control Service a command landed when it did not.
        var equipment = new FakeEquipment { NextResult = SetpointResult.RejectedState };
        var store = new ModbusTelemetryDataStore(equipment, writable: true);

        Assert.Equal(6, CodeOf(() => store.HoldingRegisters.WritePoints(0, Setpoint(600))));
        Assert.Empty(equipment.Writes);
    }

    [Fact]
    public void CoilsAreNotPartOfThisContract()
    {
        var store = new ModbusTelemetryDataStore(new FakeEquipment(), writable: true);

        Assert.Equal(2, CodeOf(() => store.CoilDiscretes.ReadPoints(0, 1)));
        Assert.Equal(2, CodeOf(() => store.CoilInputs.ReadPoints(0, 1)));
        Assert.Equal(2, CodeOf(() => store.CoilDiscretes.WritePoints(0, [true])));
    }
}
