namespace Mair.EdgeGateway;

/// <summary>
/// Turns one protocol sample into a canonical telemetry event, following the normal flow in
/// <c>EDGE_GATEWAY.md</c> §10. One instance per equipment: steps 4, 5 and 7 are stateful.
/// <para>
/// Step 3's "BAD ⇒ null" is absolute. Substituting a last-known or zero value would feed the safety
/// gates a fabricated reading, which is the failure ADR-0018 exists to prevent.
/// </para>
/// </summary>
public sealed class TelemetryNormaliser
{
    private const int FrozenThreshold = 30;      // identical samples (§15)
    private const double OutlierSigma = 6.0;     // over the trailing window (§15)
    private const int TrailingWindowSamples = 600; // 60 s at 100 ms

    private static readonly Channel[] Measurements =
    [
        Channel.TemperatureC, Channel.VibrationRms, Channel.CurrentA, Channel.VoltageV,
        Channel.Rpm, Channel.TorqueNm, Channel.OperationRatePct,
    ];

    private readonly string _equipmentId;
    private readonly string _producer;
    private readonly TimeProvider _time;
    private readonly Func<Guid> _newId;
    private readonly TimeSpan _clockSkewBudget;

    private readonly Dictionary<Channel, double> _lastValue = [];
    private readonly Dictionary<Channel, int> _repeatCount = [];
    private readonly Dictionary<Channel, Queue<double>> _trailing = [];

    private ulong? _lastSequence;
    private uint? _lastSourceEpochMs;
    private bool _bufferOverflowPending;

    public TelemetryNormaliser(
        string equipmentId,
        string producer,
        TimeProvider? timeProvider = null,
        Func<Guid>? idFactory = null,
        TimeSpan? clockSkewBudget = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(equipmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(producer);

        _equipmentId = equipmentId;
        _producer = producer;
        _time = timeProvider ?? TimeProvider.System;
        _newId = idFactory ?? Guid.NewGuid;
        _clockSkewBudget = clockSkewBudget ?? TimeSpan.FromMilliseconds(250);

        foreach (var channel in Measurements)
        {
            _trailing[channel] = new Queue<double>(TrailingWindowSamples);
        }
    }

    public long SequenceGaps { get; private set; }

    /// <summary>
    /// Records that events were dropped from the egress buffer. The flag rides on the **next**
    /// event rather than being lost with the dropped ones, so the loss is reported rather than
    /// silent (§11, D-02).
    /// </summary>
    public void NoteBufferOverflowDrop() => _bufferOverflowPending = true;

    /// <summary>
    /// Normalises a Modbus block. There is no source timestamp, so <c>eventTimeUtc =
    /// ingestTimeUtc</c> and <c>TIMESTAMP_SYNTHESISED</c> is **always** raised (§2.6). The
    /// consequence, stated in the contract: freshness for Modbus equipment measures
    /// gateway-to-consumer latency only, never sensor-to-consumer.
    /// </summary>
    public CanonicalTelemetry Normalise(ModbusFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var ingest = _time.GetUtcNow();
        var flags = new List<ChannelFlag> { new(Channel.Event, QualityFlag.TimestampSynthesised) };
        var values = new Dictionary<Channel, double?>();

        foreach (var channel in Measurements)
        {
            values[channel] = frame.IsValid(channel)
                ? Evaluate(channel, frame[channel], flags)
                : Missing(channel, flags);
        }

        AppendSequenceFlags(frame.Sequence, frame.SourceEpochMs, flags);
        AppendBufferOverflowFlag(flags);

        return Build(values, flags, frame.Sequence, ingest, ingest,
            SourceProtocol.ModbusTcp, StateFrom(frame.EquipmentStateCode));
    }

    /// <summary>
    /// Normalises an OPC UA sample. Unlike Modbus this carries a source timestamp and per-node
    /// status, so <paramref name="sourceTimeUtc"/> is authoritative and a reversal against the
    /// ingest clock is flagged rather than hidden.
    /// </summary>
    public CanonicalTelemetry Normalise(
        IReadOnlyDictionary<Channel, double?> readings,
        ulong sequence,
        uint sourceEpochMs,
        EquipmentState state,
        DateTimeOffset sourceTimeUtc)
    {
        ArgumentNullException.ThrowIfNull(readings);

        var ingest = _time.GetUtcNow();
        var flags = new List<ChannelFlag>();
        var values = new Dictionary<Channel, double?>();

        foreach (var channel in Measurements)
        {
            var reading = readings.TryGetValue(channel, out var v) ? v : null;
            values[channel] = reading is null
                ? Missing(channel, flags)
                : Evaluate(channel, reading.Value, flags);
        }

        // GAP-063: a source timestamp after the ingest clock beyond the skew budget is a real
        // condition, not something to normalise away.
        if (sourceTimeUtc - ingest > _clockSkewBudget)
        {
            flags.Add(new ChannelFlag(Channel.Event, QualityFlag.TimestampReversed));
        }

        AppendSequenceFlags(sequence, sourceEpochMs, flags);
        AppendBufferOverflowFlag(flags);

        return Build(values, flags, sequence, sourceTimeUtc, ingest, SourceProtocol.OpcUa, state);
    }

    private static double? Missing(Channel channel, List<ChannelFlag> flags)
    {
        flags.Add(new ChannelFlag(channel, QualityFlag.SensorMissing));
        return null;
    }

    /// <summary>Steps 2, 4 and 5 of the normal flow, in that order.</summary>
    private double? Evaluate(Channel channel, double value, List<ChannelFlag> flags)
    {
        if (!double.IsFinite(value))
        {
            flags.Add(new ChannelFlag(channel, QualityFlag.ValueNotFinite));
            return null;
        }

        if (!ChannelRange.Contains(channel, value))
        {
            // The flag table gives this "Value: null". An out-of-range reading is not trustworthy,
            // and clamping it into range would be the fabrication ADR-0018 forbids.
            flags.Add(new ChannelFlag(channel, QualityFlag.ValueOutOfRange));
            TrackFreshness(channel, value);
            return null;
        }

        if (IsFrozen(channel, value))
        {
            flags.Add(new ChannelFlag(channel, QualityFlag.SensorFrozen));
        }
        else if (IsOutlier(channel, value))
        {
            flags.Add(new ChannelFlag(channel, QualityFlag.OutlierSuspected));
        }

        TrackFreshness(channel, value);
        return value;
    }

    /// <summary>
    /// Step 4. <c>operationRatePct</c> is exempt: a machine legitimately holds a constant rate for
    /// hours, so flagging it frozen would mark every steady machine bad.
    /// </summary>
    private bool IsFrozen(Channel channel, double value)
    {
        if (channel == Channel.OperationRatePct)
        {
            return false;
        }

        return _lastValue.TryGetValue(channel, out var last)
               && last.Equals(value)
               && _repeatCount.GetValueOrDefault(channel) + 1 >= FrozenThreshold;
    }

    /// <summary>Step 5. Needs a full window first, or startup noise reads as anomalous.</summary>
    private bool IsOutlier(Channel channel, double value)
    {
        var window = _trailing[channel];
        if (window.Count < TrailingWindowSamples)
        {
            return false;
        }

        var mean = window.Average();
        var variance = window.Sum(v => (v - mean) * (v - mean)) / window.Count;
        var sigma = Math.Sqrt(variance);

        return sigma > 0 && Math.Abs(value - mean) > OutlierSigma * sigma;
    }

    private void TrackFreshness(Channel channel, double value)
    {
        _repeatCount[channel] = _lastValue.TryGetValue(channel, out var last) && last.Equals(value)
            ? _repeatCount.GetValueOrDefault(channel) + 1
            : 0;
        _lastValue[channel] = value;

        var window = _trailing[channel];
        window.Enqueue(value);
        if (window.Count > TrailingWindowSamples)
        {
            window.Dequeue();
        }
    }

    /// <summary>
    /// Step 7. A restart resets <c>sequence</c> **and** <c>sourceEpochMs</c> together (§14 and
    /// `OT_PROTOCOL_MAPPING.md` §2.3), and so does a 2^32 ms epoch wrap. Neither is a gap. A
    /// detector that flagged every sequence decrease would cry wolf on every restart.
    /// </summary>
    private void AppendSequenceFlags(ulong sequence, uint sourceEpochMs, List<ChannelFlag> flags)
    {
        if (_lastSequence is { } previous)
        {
            var epochWentBackwards = _lastSourceEpochMs is { } lastEpoch && sourceEpochMs < lastEpoch;

            if (sequence == previous)
            {
                flags.Add(new ChannelFlag(Channel.Event, QualityFlag.DuplicateSuspected));
            }
            else if (sequence < previous)
            {
                if (!epochWentBackwards)
                {
                    // Sequence went backwards without the epoch doing the same: the two signals
                    // disagree, which is not a restart and must not be silently accepted.
                    flags.Add(new ChannelFlag(Channel.Event, QualityFlag.SequenceGap));
                    SequenceGaps++;
                }
            }
            else if (sequence - previous > 1)
            {
                flags.Add(new ChannelFlag(Channel.Event, QualityFlag.SequenceGap));
                SequenceGaps++;
            }
        }

        _lastSequence = sequence;
        _lastSourceEpochMs = sourceEpochMs;
    }

    private void AppendBufferOverflowFlag(List<ChannelFlag> flags)
    {
        if (!_bufferOverflowPending)
        {
            return;
        }

        flags.Add(new ChannelFlag(Channel.Event, QualityFlag.BufferOverflowDrop));
        _bufferOverflowPending = false;
    }

    private static EquipmentState StateFrom(int code) => code switch
    {
        0 => EquipmentState.Offline,
        1 => EquipmentState.Connecting,
        2 => EquipmentState.Idle,
        3 => EquipmentState.Running,
        4 => EquipmentState.Degraded,
        5 => EquipmentState.Fault,
        6 => EquipmentState.Stopping,
        _ => throw new ArgumentOutOfRangeException(nameof(code), code, "unknown equipment state code (§3)"),
    };

    private CanonicalTelemetry Build(
        Dictionary<Channel, double?> values,
        List<ChannelFlag> flags,
        ulong sequence,
        DateTimeOffset eventTime,
        DateTimeOffset ingestTime,
        SourceProtocol protocol,
        EquipmentState state) => new()
    {
        EventId = _newId(),
        EquipmentId = _equipmentId,
        EventTimeUtc = eventTime,
        IngestTimeUtc = ingestTime,
        Sequence = sequence,
        Producer = _producer,
        SourceProtocol = protocol,
        QualityOverall = Quality.Derive(flags),
        Flags = flags,
        TemperatureC = values[Channel.TemperatureC],
        VibrationRms = values[Channel.VibrationRms],
        CurrentA = values[Channel.CurrentA],
        VoltageV = values[Channel.VoltageV],
        Rpm = values[Channel.Rpm],
        TorqueNm = values[Channel.TorqueNm],
        OperationRatePct = values[Channel.OperationRatePct],
        EquipmentState = state,
        CorrelationId = _newId(),
    };
}
