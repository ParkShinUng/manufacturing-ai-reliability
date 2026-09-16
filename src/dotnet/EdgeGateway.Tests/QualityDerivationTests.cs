namespace Mair.EdgeGateway.Tests;

/// <summary>
/// <b>PROP-06</b> — for any flag combination, <c>quality.overall</c> equals the derivation rule
/// (TIME_AND_DATA_QUALITY.md §6), and <b>OT-003 / AC-022</b>'s quality half.
/// <para>
/// The expectations here are written from the specification tables, not from the implementation:
/// a test that recomputes the rule the same way the code does would agree with any bug.
/// </para>
/// </summary>
public sealed class QualityDerivationTests
{
    private static readonly Channel[] Measurements =
    [
        Channel.TemperatureC, Channel.VibrationRms, Channel.CurrentA, Channel.VoltageV,
        Channel.Rpm, Channel.TorqueNm, Channel.OperationRatePct,
    ];

    private static readonly QualityFlag[] AllFlags = Enum.GetValues<QualityFlag>();

    /// <summary>The §4 safety-required map, transcribed from the document.</summary>
    private static bool SpecSaysSafetyRequired(Channel c) =>
        c is Channel.VibrationRms or Channel.TemperatureC or Channel.CurrentA
          or Channel.Rpm or Channel.OperationRatePct;

    /// <summary>The §5 "Contributes" column, transcribed from the document.</summary>
    private static QualityContribution SpecContribution(QualityFlag f) => f switch
    {
        QualityFlag.SensorMissing or QualityFlag.SensorDisconnected or QualityFlag.SensorFrozen
            or QualityFlag.ValueOutOfRange or QualityFlag.ValueNotFinite => QualityContribution.Bad,
        QualityFlag.TimestampSynthesised => QualityContribution.None,
        _ => QualityContribution.Uncertain,
    };

    private static QualityOverall SpecExpected(Channel c, QualityFlag f) => SpecContribution(f) switch
    {
        QualityContribution.Bad when SpecSaysSafetyRequired(c) => QualityOverall.Bad,
        QualityContribution.Bad => QualityOverall.Uncertain,
        QualityContribution.Uncertain => QualityOverall.Uncertain,
        _ => QualityOverall.Good,
    };

    [Fact]
    public void NoFlagsIsGood()
    {
        // The negative case OT-003 requires: without it, an implementation that flagged everything
        // would satisfy every other assertion here.
        Assert.Equal(QualityOverall.Good, Quality.Derive([]));
    }

    [Fact]
    public void EverySingleChannelFlagPairMatchesTheSpecification()
    {
        foreach (var channel in Measurements)
        {
            foreach (var flag in AllFlags)
            {
                Assert.Equal(
                    SpecExpected(channel, flag),
                    Quality.Derive([new ChannelFlag(channel, flag)]));
            }
        }
    }

    [Fact]
    public void EveryPairOfFlagsIsTheWorseOfTheTwo()
    {
        // Independent of how Derive is written: severity must compose as a maximum. 7 channels x
        // 12 flags = 84 single flags, so 84^2 = 7056 combinations.
        foreach (var c1 in Measurements)
        foreach (var f1 in AllFlags)
        foreach (var c2 in Measurements)
        foreach (var f2 in AllFlags)
        {
            var expected = (QualityOverall)Math.Max(
                (int)SpecExpected(c1, f1), (int)SpecExpected(c2, f2));

            Assert.Equal(expected, Quality.Derive([new ChannelFlag(c1, f1), new ChannelFlag(c2, f2)]));
        }
    }

    [Fact]
    public void ADeadSafetyRequiredSensorIsBad_ButADeadAdvisoryOneIsOnlyUncertain()
    {
        // The asymmetry is the point of the safety-required map: a dead torque sensor suspends AI
        // authority without hard-rejecting the equipment, a dead vibration sensor rejects it.
        Assert.Equal(QualityOverall.Bad,
            Quality.Derive([new ChannelFlag(Channel.VibrationRms, QualityFlag.SensorMissing)]));

        Assert.Equal(QualityOverall.Uncertain,
            Quality.Derive([new ChannelFlag(Channel.TorqueNm, QualityFlag.SensorMissing)]));
    }

    [Fact]
    public void TimestampSynthesisedNeverDegradesQuality_OnAnyChannel()
    {
        // Every Modbus event carries it permanently. If it contributed, all Modbus equipment would
        // be UNCERTAIN forever and AI would be disabled on it (TIME_AND_DATA_QUALITY.md §5).
        foreach (var channel in Measurements.Append(Channel.Event))
        {
            Assert.Equal(QualityOverall.Good,
                Quality.Derive([new ChannelFlag(channel, QualityFlag.TimestampSynthesised)]));
        }

        // And it must not mask a real flag sitting beside it.
        Assert.Equal(QualityOverall.Bad, Quality.Derive(
        [
            new ChannelFlag(Channel.Rpm, QualityFlag.TimestampSynthesised),
            new ChannelFlag(Channel.Rpm, QualityFlag.SensorMissing),
        ]));
    }

    [Fact]
    public void EventScopedFlagsAreNeverSafetyRequired()
    {
        // SEQUENCE_GAP and BUFFER_OVERFLOW_DROP belong to the event, not to a channel, so they can
        // only ever reach UNCERTAIN - treating __event__ as safety-required would let a single
        // dropped sample hard-reject the equipment.
        Assert.False(Quality.IsSafetyRequired(Channel.Event));

        Assert.Equal(QualityOverall.Uncertain,
            Quality.Derive([new ChannelFlag(Channel.Event, QualityFlag.SequenceGap)]));
        Assert.Equal(QualityOverall.Uncertain,
            Quality.Derive([new ChannelFlag(Channel.Event, QualityFlag.BufferOverflowDrop)]));
    }

    [Fact]
    public void TheClosedVocabularyIsExactlyTheTwelveDocumentedFlags()
    {
        // An unlisted flag is a contract violation (GAP-061), so the enum size is asserted rather
        // than trusted - adding one without updating telemetry.schema.json fails here.
        Assert.Equal(12, AllFlags.Length);
        Assert.Equal(8, Enum.GetValues<Channel>().Length); // 7 measurements + __event__
    }

    [Fact]
    public void EveryFlagAndChannelHasADefinedContribution()
    {
        // Guards the switch expressions against a silently unhandled new member.
        foreach (var flag in AllFlags)
        {
            _ = Quality.ContributionOf(flag);
        }

        foreach (var channel in Enum.GetValues<Channel>())
        {
            _ = Quality.IsSafetyRequired(channel);
        }
    }
}
