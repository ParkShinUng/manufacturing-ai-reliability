namespace Mair.EdgeGateway;

/// <summary>One decoded Modbus block read, before normalisation into a canonical event.</summary>
public sealed record ModbusFrame
{
    public required double Rpm { get; init; }
    public required double TorqueNm { get; init; }
    public required double CurrentA { get; init; }
    public required double VoltageV { get; init; }
    public required double TemperatureC { get; init; }
    public required double VibrationRms { get; init; }
    public required double OperationRatePct { get; init; }

    public required int EquipmentStateCode { get; init; }

    /// <summary>Bit set = channel valid (§2.5). A clear bit means emit <c>null</c> + <c>SENSOR_MISSING</c>.</summary>
    public required ushort QualityBitmap { get; init; }

    public required ulong Sequence { get; init; }
    public required uint SourceEpochMs { get; init; }

    /// <summary>Bit 0 = fault injection active, demo profile only. Bits 1-15 reserved (§2.3 +22).</summary>
    public required ushort StatusBitmap { get; init; }

    public bool FaultInjectionActive => (StatusBitmap & 0x0001) != 0;

    public double this[Channel channel] => channel switch
    {
        Channel.Rpm => Rpm,
        Channel.TorqueNm => TorqueNm,
        Channel.CurrentA => CurrentA,
        Channel.VoltageV => VoltageV,
        Channel.TemperatureC => TemperatureC,
        Channel.VibrationRms => VibrationRms,
        Channel.OperationRatePct => OperationRatePct,
        _ => throw new ArgumentOutOfRangeException(nameof(channel)),
    };

    /// <summary>§2.5 bit order: rpm, torque, current, voltage, temperature, vibration, rate.</summary>
    public bool IsValid(Channel channel) => channel switch
    {
        Channel.Rpm => (QualityBitmap & (1 << 0)) != 0,
        Channel.TorqueNm => (QualityBitmap & (1 << 1)) != 0,
        Channel.CurrentA => (QualityBitmap & (1 << 2)) != 0,
        Channel.VoltageV => (QualityBitmap & (1 << 3)) != 0,
        Channel.TemperatureC => (QualityBitmap & (1 << 4)) != 0,
        Channel.VibrationRms => (QualityBitmap & (1 << 5)) != 0,
        Channel.OperationRatePct => (QualityBitmap & (1 << 6)) != 0,
        _ => throw new ArgumentOutOfRangeException(nameof(channel)),
    };
}

/// <summary>
/// Decodes the 23-register input block defined in <c>OT_PROTOCOL_MAPPING.md</c> §2.3.
/// <para>
/// This map is written out here **and** in the simulator's encoder, deliberately not shared. The
/// two sides of an OT link are independent implementations in reality, and `AC-021` exists to catch
/// a disagreement between them by comparing engineering values against OPC UA. A shared codec would
/// make that test pass by construction and prove nothing.
/// </para>
/// </summary>
public static class ModbusRegisterDecoder
{
    /// <summary>Offsets +0…+22 inclusive.</summary>
    public const int BlockLength = 23;

    /// <summary>Base input-register address for an equipment index (§2.3).</summary>
    public static int BaseAddress(int equipmentIndex) => 40 * equipmentIndex;

    public static ModbusFrame Decode(ReadOnlySpan<ushort> block)
    {
        if (block.Length != BlockLength)
        {
            throw new ArgumentException(
                $"a Modbus telemetry block is exactly {BlockLength} registers (+0..+22); got {block.Length}.",
                nameof(block));
        }

        return new ModbusFrame
        {
            Rpm = ReadInt32(block, 0) / 10.0,
            TorqueNm = ReadInt32(block, 2) / 10.0,
            CurrentA = ReadInt32(block, 4) / 100.0,
            VoltageV = ReadInt32(block, 6) / 10.0,
            TemperatureC = ReadInt32(block, 8) / 10.0,
            VibrationRms = ReadInt32(block, 10) / 100.0,
            OperationRatePct = ReadInt32(block, 12) / 10.0,
            EquipmentStateCode = block[14],
            QualityBitmap = block[15],
            Sequence = ReadUInt64(block, 16),
            SourceEpochMs = ReadUInt32(block, 20),
            StatusBitmap = block[22],
        };
    }

    // Word order is HIGH WORD FIRST (§2.1). This is the classic Modbus interoperability trap -
    // roughly half of real devices do the opposite - so it is spelled out rather than inferred
    // from a BitConverter call whose endianness depends on the machine.
    private static int ReadInt32(ReadOnlySpan<ushort> b, int offset)
        => unchecked((int)(((uint)b[offset] << 16) | b[offset + 1]));

    private static uint ReadUInt32(ReadOnlySpan<ushort> b, int offset)
        => ((uint)b[offset] << 16) | b[offset + 1];

    private static ulong ReadUInt64(ReadOnlySpan<ushort> b, int offset)
        => ((ulong)b[offset] << 48) | ((ulong)b[offset + 1] << 32)
           | ((ulong)b[offset + 2] << 16) | b[offset + 3];
}
