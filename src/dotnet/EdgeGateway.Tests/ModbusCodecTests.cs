using Mair.EdgeGateway;
using Sim = Mair.EquipmentSimulator;

namespace Mair.EdgeGateway.Tests;

/// <summary>
/// The Modbus half of <b>OT-002 / AC-021</b>: the simulator's encoder and the gateway's decoder are
/// independent implementations of `OT_PROTOCOL_MAPPING.md` §2.3, and they must agree on engineering
/// values. These tests compare them directly; the full AC-021 comparison against OPC UA arrives
/// with the protocol servers.
/// </summary>
public sealed class ModbusCodecTests
{
    private static Sim.RawSample Sample(
        double? rpm = 1800, double? torque = 42, double? current = 12, double? voltage = 400,
        double? temperature = 48, double? vibration = 2.2, double? rate = 100,
        ulong sequence = 12345, uint epochMs = 67890,
        Sim.EquipmentState state = Sim.EquipmentState.Running) => new()
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

    [Fact]
    public void BlockIsTwentyThreeRegisters()
    {
        // 22 would silently drop statusBitmap at +22, which is where it was moved to stop it
        // aliasing the high word of the 32-bit sourceEpochMs (CODEX-R3-005).
        Assert.Equal(23, Sim.ModbusRegisterEncoder.BlockLength);
        Assert.Equal(23, ModbusRegisterDecoder.BlockLength);
        Assert.Equal(23, Sim.ModbusRegisterEncoder.Encode(Sample()).Length);
    }

    [Fact]
    public void EngineeringValuesSurviveTheRoundTrip()
    {
        var frame = ModbusRegisterDecoder.Decode(Sim.ModbusRegisterEncoder.Encode(
            Sample(rpm: 1234.5, torque: -7.3, current: 18.62, voltage: 398.7,
                   temperature: 133.3, vibration: 28.61, rate: 60.4)));

        // Each within its documented resolution (§1.2), which is also the scale factor's precision.
        Assert.Equal(1234.5, frame.Rpm, 1);
        Assert.Equal(-7.3, frame.TorqueNm, 1);
        Assert.Equal(18.62, frame.CurrentA, 2);
        Assert.Equal(398.7, frame.VoltageV, 1);
        Assert.Equal(133.3, frame.TemperatureC, 1);
        Assert.Equal(28.61, frame.VibrationRms, 2);
        Assert.Equal(60.4, frame.OperationRatePct, 1);
    }

    [Fact]
    public void NegativeTorqueSurvives_SoTheSignedEncodingIsRealNotAssumed()
    {
        // torqueNm is the one channel whose valid range goes below zero (-10..150): regenerative
        // braking is physical. An unsigned encoding would turn -7.3 into ~429 million.
        var frame = ModbusRegisterDecoder.Decode(Sim.ModbusRegisterEncoder.Encode(Sample(torque: -9.9)));

        Assert.Equal(-9.9, frame.TorqueNm, 1);
        Assert.True(frame.TorqueNm < 0);
    }

    [Fact]
    public void WordOrderIsHighWordFirst()
    {
        // The classic Modbus interoperability trap: roughly half of real devices do the opposite,
        // and in a scaled integer a word-order error produces a plausible wrong magnitude rather
        // than obvious garbage. Asserted against the raw registers, not through the round trip -
        // a round trip agrees with itself even when both ends are wrong.
        var block = Sim.ModbusRegisterEncoder.Encode(Sample(sequence: 0x0001_0002_0003_0004UL, epochMs: 0xAAAA_BBBB));

        Assert.Equal(0x0001, block[16]);
        Assert.Equal(0x0002, block[17]);
        Assert.Equal(0x0003, block[18]);
        Assert.Equal(0x0004, block[19]);
        Assert.Equal(0xAAAA, block[20]);
        Assert.Equal(0xBBBB, block[21]);
    }

    [Fact]
    public void SequenceAndEpochSurviveTheirFullRange()
    {
        var frame = ModbusRegisterDecoder.Decode(Sim.ModbusRegisterEncoder.Encode(
            Sample(sequence: ulong.MaxValue, epochMs: uint.MaxValue)));

        Assert.Equal(ulong.MaxValue, frame.Sequence);
        Assert.Equal(uint.MaxValue, frame.SourceEpochMs);
    }

    [Fact]
    public void SourceEpochOccupiesTwoRegisters_AndStatusBitmapIsAtPlus22()
    {
        // The collision this layout exists to prevent: statusBitmap at +21 would have aliased the
        // high word of sourceEpochMs. A large epoch must not disturb the status bitmap.
        var block = Sim.ModbusRegisterEncoder.Encode(Sample(epochMs: uint.MaxValue));

        Assert.Equal(0xFFFF, block[20]);
        Assert.Equal(0xFFFF, block[21]);
        Assert.Equal(0x0000, block[22]);

        block[22] = 0x0001; // the host sets bit 0 when fault injection is active
        var frame = ModbusRegisterDecoder.Decode(block);

        Assert.Equal(uint.MaxValue, frame.SourceEpochMs);
        Assert.True(frame.FaultInjectionActive);
    }

    [Fact]
    public void NullMeasurementClearsItsValidityBit_AndIsNotEncodedAsZero()
    {
        // ADR-0018: a missing reading must not become a value. Modbus has no null, so the contract
        // carries it in the quality bitmap (§2.5) and the gateway turns a clear bit into null.
        var block = Sim.ModbusRegisterEncoder.Encode(Sample(torque: null, vibration: null));
        var frame = ModbusRegisterDecoder.Decode(block);

        Assert.False(frame.IsValid(Channel.TorqueNm));
        Assert.False(frame.IsValid(Channel.VibrationRms));

        Assert.True(frame.IsValid(Channel.Rpm));
        Assert.True(frame.IsValid(Channel.CurrentA));
        Assert.True(frame.IsValid(Channel.VoltageV));
        Assert.True(frame.IsValid(Channel.TemperatureC));
        Assert.True(frame.IsValid(Channel.OperationRatePct));
    }

    [Fact]
    public void EveryChannelHasItsOwnValidityBit_InTheDocumentedOrder()
    {
        // A bitmap built in the wrong order would still pass a test that clears only one channel.
        Channel[] order =
        [
            Channel.Rpm, Channel.TorqueNm, Channel.CurrentA, Channel.VoltageV,
            Channel.TemperatureC, Channel.VibrationRms, Channel.OperationRatePct,
        ];

        for (var bit = 0; bit < order.Length; bit++)
        {
            var block = Sim.ModbusRegisterEncoder.Encode(Sample());
            block[15] = (ushort)(1 << bit); // only this channel valid
            var frame = ModbusRegisterDecoder.Decode(block);

            foreach (var channel in order)
            {
                Assert.Equal(channel == order[bit], frame.IsValid(channel));
            }
        }
    }

    [Theory]
    [InlineData(Sim.EquipmentState.Offline, 0)]
    [InlineData(Sim.EquipmentState.Connecting, 1)]
    [InlineData(Sim.EquipmentState.Idle, 2)]
    [InlineData(Sim.EquipmentState.Running, 3)]
    [InlineData(Sim.EquipmentState.Degraded, 4)]
    [InlineData(Sim.EquipmentState.Fault, 5)]
    [InlineData(Sim.EquipmentState.Stopping, 6)]
    public void StateEnumValuesAreFrozen(Sim.EquipmentState state, int code)
    {
        // §3: "Values are frozen. Appending is allowed; renumbering is a breaking change."
        var frame = ModbusRegisterDecoder.Decode(Sim.ModbusRegisterEncoder.Encode(Sample(state: state)));

        Assert.Equal(code, frame.EquipmentStateCode);
    }

    [Fact]
    public void BaseAddressIsFortyPerEquipment_OnBothSides()
    {
        // Without a per-equipment base every machine would alias the same registers.
        for (var i = 0; i < 20; i++)
        {
            Assert.Equal(40 * i, Sim.ModbusRegisterEncoder.BaseAddress(i));
            Assert.Equal(40 * i, ModbusRegisterDecoder.BaseAddress(i));
        }

        // 23 registers inside a 40-register stride: no equipment can read into its neighbour.
        Assert.True(ModbusRegisterDecoder.BlockLength < 40);
    }

    [Fact]
    public void AWrongLengthBlockIsRejected_NotSilentlyPadded()
    {
        Assert.Throws<ArgumentException>(() => ModbusRegisterDecoder.Decode(new ushort[22]));
        Assert.Throws<ArgumentException>(() => ModbusRegisterDecoder.Decode(new ushort[24]));
    }
}
