using NModbus;

namespace Mair.EquipmentSimulator.Protocols;

/// <summary>
/// How the Modbus server reaches the simulated equipment. Keeps the protocol layer from knowing
/// anything about the L3 core beyond these two operations.
/// </summary>
public interface IEquipmentAccess
{
    /// <summary>Number of equipment served, so an address can be bounds-checked.</summary>
    int Count { get; }

    /// <summary>The most recent sample, or <c>null</c> if the equipment is not answering.</summary>
    RawSample? Latest(int equipmentIndex);

    SetpointResult WriteSetpoint(int equipmentIndex, double ratePct);
}

/// <summary>
/// The Modbus slave data store for one listener, implementing <c>OT_PROTOCOL_MAPPING.md</c> §2.
/// <para>
/// Two instances exist per simulator (OD-003): the one on port <b>5020</b> is constructed with
/// <c>writable: false</c> and refuses every write with exception <c>0x01</c>, and it is the one the
/// Edge Gateway connects to. The one on <b>5021</b> accepts the single documented write and is the
/// Control Service's. Modbus TCP carries no identity, so this is the only way the read-only
/// guarantee can be enforced by the server rather than trusted of the client.
/// </para>
/// </summary>
public sealed class ModbusTelemetryDataStore : ISlaveDataStore
{
    /// <summary>Address stride per equipment (§2.3). 23 registers used, so no equipment reads into its neighbour.</summary>
    public const int AddressStride = 40;

    /// <summary>Holding-register stride per equipment (§2.4).</summary>
    public const int HoldingStride = 4;

    /// <summary>Setpoint scale: ×10, so 0–1000 is 0.0–100.0 % (§2.4).</summary>
    public const int SetpointScale = 10;

    private readonly IEquipmentAccess _equipment;
    private readonly bool _writable;

    public ModbusTelemetryDataStore(IEquipmentAccess equipment, bool writable)
    {
        ArgumentNullException.ThrowIfNull(equipment);
        _equipment = equipment;
        _writable = writable;

        InputRegisters = new InputRegisterSource(equipment);
        HoldingRegisters = new HoldingRegisterSource(this);
    }

    public IPointSource<ushort> InputRegisters { get; }

    public IPointSource<ushort> HoldingRegisters { get; }

    // Coils are not part of this contract. Reading or writing one is an addressing error, not a
    // silently empty result.
    public IPointSource<bool> CoilDiscretes { get; } = new UnmappedBits();

    public IPointSource<bool> CoilInputs { get; } = new UnmappedBits();

    private sealed class InputRegisterSource(IEquipmentAccess equipment) : IPointSource<ushort>
    {
        public ushort[] ReadPoints(ushort startAddress, ushort numberOfPoints)
        {
            // The gateway reads exactly one 23-register block per equipment, starting at the base.
            // A read that straddles two equipment, or starts mid-block, would return a physically
            // meaningless mixture, so it is refused rather than served.
            if (startAddress % AddressStride != 0)
            {
                throw new InvalidModbusRequestException(SlaveExceptionCodes.IllegalDataAddress);
            }

            if (numberOfPoints != ModbusRegisterEncoder.BlockLength)
            {
                throw new InvalidModbusRequestException(SlaveExceptionCodes.IllegalDataAddress);
            }

            var index = startAddress / AddressStride;
            if (index < 0 || index >= equipment.Count)
            {
                throw new InvalidModbusRequestException(SlaveExceptionCodes.IllegalDataAddress);
            }

            var sample = equipment.Latest(index)
                         ?? throw new InvalidModbusRequestException(SlaveExceptionCodes.SlaveDeviceFailure);

            return ModbusRegisterEncoder.Encode(sample);
        }

        public void WritePoints(ushort startAddress, ushort[] points)
            // Input registers are read-only in the Modbus protocol itself; reaching here at all is
            // a malformed request.
            => throw new InvalidModbusRequestException(SlaveExceptionCodes.IllegalDataAddress);
    }

    private sealed class HoldingRegisterSource(ModbusTelemetryDataStore store) : IPointSource<ushort>
    {
        public ushort[] ReadPoints(ushort startAddress, ushort numberOfPoints)
        {
            var index = Index(startAddress);
            var sample = store._equipment.Latest(index);

            // Reading back the setpoint is the register's own meaning. Two registers, high word
            // first, ×10 — the same encoding a write uses.
            var scaled = (int)Math.Round((sample?.OperationRatePct ?? 0) * SetpointScale,
                MidpointRounding.AwayFromZero);

            var block = new ushort[] { (ushort)((uint)scaled >> 16), (ushort)((uint)scaled & 0xFFFF), 0, 0 };

            if (numberOfPoints > block.Length)
            {
                throw new InvalidModbusRequestException(SlaveExceptionCodes.IllegalDataAddress);
            }

            return block[..numberOfPoints];
        }

        public void WritePoints(ushort startAddress, ushort[] points)
        {
            // OD-003: the read-only listener has no write path at all. This is what makes "the
            // gateway cannot write" true by construction rather than by trusting the gateway.
            if (!store._writable)
            {
                throw new InvalidModbusRequestException(SlaveExceptionCodes.IllegalFunction);
            }

            // §2.4: function code 16 only, quantity exactly 2, starting exactly at the base.
            // NModbus routes both FC06 and FC16 here, and FC06 arrives as a single point — which is
            // precisely the request that must not be honoured: operationRateSetpointPct is a
            // two-register int32, so a one-register write would set a DIFFERENT rate the equipment
            // would then act on.
            if (points.Length != 2 || startAddress % HoldingStride != 0)
            {
                throw new InvalidModbusRequestException(SlaveExceptionCodes.IllegalDataAddress);
            }

            var index = Index(startAddress);
            var raw = unchecked((int)(((uint)points[0] << 16) | points[1]));

            // Rejected, never clamped (§2.4, EQUIPMENT_SIMULATOR.md §11). Clamping would mask a
            // control-path defect the Control Service bounds check should have caught first.
            if (raw is < 0 or > 1000)
            {
                throw new InvalidModbusRequestException(SlaveExceptionCodes.IllegalDataValue);
            }

            var result = store._equipment.WriteSetpoint(index, raw / (double)SetpointScale);

            // The equipment may still refuse: out of range is already excluded above, so this is a
            // state refusal — FAULT, STOPPING, OFFLINE. Reported rather than swallowed.
            if (result != SetpointResult.Accepted)
            {
                throw new InvalidModbusRequestException(result == SetpointResult.RejectedOutOfRange
                    ? SlaveExceptionCodes.IllegalDataValue
                    : SlaveExceptionCodes.SlaveDeviceBusy);
            }
        }

        private int Index(ushort startAddress)
        {
            var index = startAddress / HoldingStride;
            return index >= 0 && index < store._equipment.Count
                ? index
                : throw new InvalidModbusRequestException(SlaveExceptionCodes.IllegalDataAddress);
        }
    }

    private sealed class UnmappedBits : IPointSource<bool>
    {
        public bool[] ReadPoints(ushort startAddress, ushort numberOfPoints)
            => throw new InvalidModbusRequestException(SlaveExceptionCodes.IllegalDataAddress);

        public void WritePoints(ushort startAddress, bool[] points)
            => throw new InvalidModbusRequestException(SlaveExceptionCodes.IllegalDataAddress);
    }
}
