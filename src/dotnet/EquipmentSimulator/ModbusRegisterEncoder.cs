namespace Mair.EquipmentSimulator;

/// <summary>
/// Encodes a <see cref="RawSample"/> into the 23-register input block defined in
/// <c>OT_PROTOCOL_MAPPING.md</c> §2.3, for the simulator's Modbus TCP server.
/// <para>
/// The gateway has its own decoder and the two are **deliberately not shared**. In a real plant the
/// device and the gateway are separate implementations, and `AC-021` proves they agree by comparing
/// engineering values against the OPC UA path. Sharing a codec would make that test pass by
/// construction and prove nothing about scaling or word order.
/// </para>
/// </summary>
public static class ModbusRegisterEncoder
{
    /// <summary>Offsets +0…+22 inclusive.</summary>
    public const int BlockLength = 23;

    public static int BaseAddress(int equipmentIndex) => 40 * equipmentIndex;

    public static ushort[] Encode(RawSample sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        var block = new ushort[BlockLength];

        // A null measurement has no register representation - Modbus carries no "no value". The
        // contract's answer is the quality bitmap: the register holds 0 and the channel's validity
        // bit is CLEARED, and the gateway turns a clear bit into null + SENSOR_MISSING (§2.5).
        // Writing 0 with the bit set would be exactly the fabricated reading ADR-0018 forbids.
        WriteInt32(block, 0, Scale(sample.Rpm, 10));
        WriteInt32(block, 2, Scale(sample.TorqueNm, 10));
        WriteInt32(block, 4, Scale(sample.CurrentA, 100));
        WriteInt32(block, 6, Scale(sample.VoltageV, 10));
        WriteInt32(block, 8, Scale(sample.TemperatureC, 10));
        WriteInt32(block, 10, Scale(sample.VibrationRms, 100));
        WriteInt32(block, 12, Scale(sample.OperationRatePct, 10));

        block[14] = (ushort)StateCode(sample.State);
        block[15] = QualityBitmap(sample);
        WriteUInt64(block, 16, sample.Sequence);
        WriteUInt32(block, 20, sample.SourceEpochMs);
        block[22] = 0; // statusBitmap: bit 0 is set by the host when fault injection is active.

        return block;
    }

    /// <summary>§3 state enum. Values are frozen; renumbering is a breaking contract change.</summary>
    public static int StateCode(EquipmentState state) => state switch
    {
        EquipmentState.Offline => 0,
        EquipmentState.Connecting => 1,
        EquipmentState.Idle => 2,
        EquipmentState.Running => 3,
        EquipmentState.Degraded => 4,
        EquipmentState.Fault => 5,
        EquipmentState.Stopping => 6,
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    /// <summary>§2.5 — bit SET means the channel is valid.</summary>
    private static ushort QualityBitmap(RawSample sample)
    {
        var bits = 0;
        if (sample.Rpm is not null) bits |= 1 << 0;
        if (sample.TorqueNm is not null) bits |= 1 << 1;
        if (sample.CurrentA is not null) bits |= 1 << 2;
        if (sample.VoltageV is not null) bits |= 1 << 3;
        if (sample.TemperatureC is not null) bits |= 1 << 4;
        if (sample.VibrationRms is not null) bits |= 1 << 5;
        if (sample.OperationRatePct is not null) bits |= 1 << 6;
        return (ushort)bits;
    }

    private static int Scale(double? value, int factor)
        => value is null ? 0 : (int)Math.Round(value.Value * factor, MidpointRounding.AwayFromZero);

    // High word first (§2.1).
    private static void WriteInt32(ushort[] b, int offset, int value)
    {
        var u = unchecked((uint)value);
        b[offset] = (ushort)(u >> 16);
        b[offset + 1] = (ushort)(u & 0xFFFF);
    }

    private static void WriteUInt32(ushort[] b, int offset, uint value)
    {
        b[offset] = (ushort)(value >> 16);
        b[offset + 1] = (ushort)(value & 0xFFFF);
    }

    private static void WriteUInt64(ushort[] b, int offset, ulong value)
    {
        b[offset] = (ushort)(value >> 48);
        b[offset + 1] = (ushort)((value >> 32) & 0xFFFF);
        b[offset + 2] = (ushort)((value >> 16) & 0xFFFF);
        b[offset + 3] = (ushort)(value & 0xFFFF);
    }
}
