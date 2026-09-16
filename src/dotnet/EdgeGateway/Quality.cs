namespace Mair.EdgeGateway;

/// <summary>
/// The seven canonical measurement channels, plus the event-scoped pseudo-channel.
/// `telemetry.schema.json` pins this vocabulary.
/// </summary>
public enum Channel
{
    TemperatureC,
    VibrationRms,
    CurrentA,
    VoltageV,
    Rpm,
    TorqueNm,
    OperationRatePct,

    /// <summary>
    /// `__event__` — for flags that belong to the event rather than to one channel,
    /// such as <see cref="QualityFlag.SequenceGap"/> and
    /// <see cref="QualityFlag.BufferOverflowDrop"/>.
    /// </summary>
    Event,
}

/// <summary>
/// The closed quality vocabulary (GAP-061). An unlisted flag is a contract violation, which is why
/// this is an enum rather than a string: v0.2 had an unconstrained string array here.
/// </summary>
public enum QualityFlag
{
    SensorMissing,
    SensorDisconnected,
    SensorFrozen,
    ValueOutOfRange,
    ValueNotFinite,
    StaleReading,
    OutlierSuspected,
    TimestampReversed,
    TimestampSynthesised,
    DuplicateSuspected,
    SequenceGap,
    BufferOverflowDrop,
}

public enum QualityOverall
{
    Good,
    Uncertain,
    Bad,
}

/// <summary>What a flag contributes to <c>quality.overall</c>. TIME_AND_DATA_QUALITY.md §5.</summary>
public enum QualityContribution
{
    None,
    Uncertain,
    Bad,
}

public readonly record struct ChannelFlag(Channel Channel, QualityFlag Flag);

/// <summary>
/// Derives <c>quality.overall</c>. The Edge Gateway is its **sole owner** (DEC-008): it is the only
/// component holding both the protocol quality and the safety-required sensor map, and the value is
/// derived, never free-form.
/// </summary>
public static class Quality
{
    /// <summary>
    /// Safety-required channels, EQUIPMENT_MODEL_AND_STATE.md §4. A disqualifying flag on one of
    /// these makes the whole event <c>BAD</c>; the same flag on an advisory channel only makes it
    /// <c>UNCERTAIN</c>.
    /// </summary>
    public static bool IsSafetyRequired(Channel channel) => channel switch
    {
        Channel.VibrationRms => true,
        Channel.TemperatureC => true,
        Channel.CurrentA => true,
        Channel.Rpm => true,
        Channel.OperationRatePct => true,
        Channel.TorqueNm => false,
        Channel.VoltageV => false,
        Channel.Event => false,
        _ => throw new ArgumentOutOfRangeException(nameof(channel)),
    };

    /// <summary>TIME_AND_DATA_QUALITY.md §5, the "Contributes" column.</summary>
    public static QualityContribution ContributionOf(QualityFlag flag) => flag switch
    {
        QualityFlag.SensorMissing => QualityContribution.Bad,
        QualityFlag.SensorDisconnected => QualityContribution.Bad,
        QualityFlag.SensorFrozen => QualityContribution.Bad,
        QualityFlag.ValueOutOfRange => QualityContribution.Bad,
        QualityFlag.ValueNotFinite => QualityContribution.Bad,

        QualityFlag.StaleReading => QualityContribution.Uncertain,
        QualityFlag.OutlierSuspected => QualityContribution.Uncertain,
        QualityFlag.TimestampReversed => QualityContribution.Uncertain,
        QualityFlag.DuplicateSuspected => QualityContribution.Uncertain,
        QualityFlag.SequenceGap => QualityContribution.Uncertain,
        QualityFlag.BufferOverflowDrop => QualityContribution.Uncertain,

        // Deliberately contributes nothing. It is the normal, permanent condition for every Modbus
        // device; letting it degrade quality would mark all Modbus equipment UNCERTAIN forever and
        // disable AI on it (TIME_AND_DATA_QUALITY.md §5).
        QualityFlag.TimestampSynthesised => QualityContribution.None,

        _ => throw new ArgumentOutOfRangeException(nameof(flag)),
    };

    /// <summary>
    /// <c>BAD</c> if any safety-required channel carries a BAD-contributing flag; otherwise
    /// <c>UNCERTAIN</c> if any channel carries an UNCERTAIN-contributing flag; otherwise
    /// <c>GOOD</c>. TIME_AND_DATA_QUALITY.md §6.
    /// <para>
    /// Note the asymmetry, which is the whole point: a BAD-contributing flag on an **advisory**
    /// channel yields <c>UNCERTAIN</c>, not <c>BAD</c> — a dead torque sensor suspends AI authority
    /// without hard-rejecting the equipment.
    /// </para>
    /// </summary>
    public static QualityOverall Derive(IEnumerable<ChannelFlag> flags)
    {
        ArgumentNullException.ThrowIfNull(flags);

        var overall = QualityOverall.Good;

        foreach (var (channel, flag) in flags)
        {
            switch (ContributionOf(flag))
            {
                case QualityContribution.Bad when IsSafetyRequired(channel):
                    return QualityOverall.Bad;

                case QualityContribution.Bad:
                case QualityContribution.Uncertain:
                    overall = QualityOverall.Uncertain;
                    break;

                case QualityContribution.None:
                default:
                    break;
            }
        }

        return overall;
    }
}
