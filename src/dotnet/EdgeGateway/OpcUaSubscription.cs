using System.Collections.Concurrent;
using Opc.Ua;
using Opc.Ua.Client;

namespace Mair.EdgeGateway;

/// <summary>
/// The OPC UA <b>subscription</b> path defined in <c>OT_PROTOCOL_MAPPING.md</c> §1.3 — the design
/// the contract specifies, as opposed to the batch read the gateway used first.
/// <para>
/// The difference is the point. A polled client reads whatever the node holds right now, so a sample
/// produced and overwritten between two polls is gone. A subscription asks the <b>server</b> to
/// sample at 50 ms and queue up to 10 values per item with discard-oldest, so a client that misses a
/// publishing cycle still receives what it missed. Measured over 60 s against a simulator producing
/// at 100 ms: 599 of 601 distinct samples with zero gaps, against 439 for the polled path (OD-005).
/// </para>
/// </summary>
public sealed class OpcUaSubscription : IAsyncDisposable
{
    // §1.3, every value from the contract.
    public const int PublishingIntervalMs = 100;   // matches the telemetry cadence
    public const int SamplingIntervalMs = 50;      // Nyquist-safe against the 100 ms publish
    public const uint QueueSize = 10;              // absorbs one publish-cycle stall without loss
    public const bool DiscardOldest = true;        // prefer fresh data; loss is flagged, not hidden
    public const uint KeepAliveCount = 5;          // ~500 ms silence detection
    public const uint LifetimeCount = 15;          // 3x keep-alive, per OPC UA convention

    /// <summary>The ten nodes a canonical sample needs. <c>RateSetpoint</c> is not one of them.</summary>
    private static readonly (string Identifier, Channel? Channel)[] Nodes =
    [
        ("Rpm", Channel.Rpm),
        ("TorqueNm", Channel.TorqueNm),
        ("CurrentA", Channel.CurrentA),
        ("VoltageV", Channel.VoltageV),
        ("TemperatureC", Channel.TemperatureC),
        ("VibrationRms", Channel.VibrationRms),
        ("OperationRatePct", Channel.OperationRatePct),
        ("State", null),
        ("SequenceNo", null),
        ("SourceEpochMs", null),
    ];

    /// <summary>The tick marker: it changes on every model step, so it is what defines a sample.</summary>
    private const string Marker = "SequenceNo";

    private readonly ISession _session;
    private readonly string _equipmentId;
    private readonly ushort _namespaceIndex;
    private readonly ConcurrentQueue<OpcUaFrame> _ready = new();
    private readonly Dictionary<uint, string> _handles = [];
    private readonly Dictionary<string, DataValue> _carried = [];
    private readonly Lock _gate = new();

    private Subscription? _subscription;

    public OpcUaSubscription(ISession session, string equipmentId, ushort namespaceIndex)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(equipmentId);

        _session = session;
        _equipmentId = equipmentId;
        _namespaceIndex = namespaceIndex;
    }

    public int ReadyCount => _ready.Count;

    /// <summary>
    /// Markers seen before every node had reported at least once. Only possible at start-up, and
    /// counted rather than filled in: inventing a value for a node that has never spoken is the
    /// fabricated reading ADR-0018 forbids.
    /// </summary>
    public long IncompleteGroupsDropped { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var subscription = new Subscription(_session.DefaultSubscription)
        {
            DisplayName = $"mair-{_equipmentId}",
            PublishingInterval = PublishingIntervalMs,
            KeepAliveCount = KeepAliveCount,
            LifetimeCount = LifetimeCount,
            PublishingEnabled = true,
            TimestampsToReturn = TimestampsToReturn.Both,

            // Assemble per publish cycle rather than per item. This is the whole coherence argument,
            // and it took three wrong turns to reach:
            //
            // 1. Group all ten items by a shared source timestamp. Produced three samples in sixty
            //    seconds — an item reports on CHANGE, and `State` sits at RUNNING all run, so a
            //    group requiring it never completed.
            // 2. Assemble on the marker from each node's LATEST value. Fast, but incoherent: if the
            //    marker for tick N arrives before the epoch for tick N, the sample pairs sequence N
            //    with epoch N-1. A sample stitched from two model steps is not a reading the
            //    equipment ever produced, which puts it alongside the substituted values ADR-0018
            //    forbids. A coherence test caught it on its first run.
            // 3. Hold the marker until every node has reported at or after its timestamp. Correct in
            //    principle, unusable in practice: a stable node never reports, so it can never
            //    supply that proof, and markers were held forever.
            //
            // Per publish cycle removes the ambiguity that defeated 2 and 3: an item absent from a
            // DataChangeNotification did not change during that cycle, and the protocol guarantees
            // it — unlike per-item delivery, where "no value yet" and "unchanged" look identical.
            FastDataChangeCallback = OnDataChange,
        };

        foreach (var (identifier, _) in Nodes)
        {
            var item = new MonitoredItem(subscription.DefaultItem)
            {
                DisplayName = identifier,
                StartNodeId = new NodeId($"Eq.{_equipmentId}.{identifier}", _namespaceIndex),
                AttributeId = Attributes.Value,
                MonitoringMode = MonitoringMode.Reporting,
                SamplingInterval = SamplingIntervalMs,
                QueueSize = QueueSize,
                DiscardOldest = DiscardOldest,

                // No deadband. §1.3 is explicit: a deadband would suppress unchanged values, and an
                // unchanged value is exactly what SENSOR_FROZEN detection needs to see.
                Filter = null,
            };

            subscription.AddItem(item);
        }

        _session.AddSubscription(subscription);
        await subscription.CreateAsync(cancellationToken);

        foreach (var item in subscription.MonitoredItems)
        {
            _handles[item.ClientHandle] = item.DisplayName;
        }

        _subscription = subscription;
    }

    /// <summary>
    /// One publish cycle. Items that changed carry their queued values in order; items that did not
    /// change are absent, and their last known value is still the value in effect.
    /// <para>
    /// When the server is catching up after a missed cycle, every changing item carries the same
    /// number of queued values, so the k-th value of each belongs to the k-th model step in the
    /// cycle. That index is what pairs a <c>SequenceNo</c> with the measurements taken alongside it.
    /// </para>
    /// </summary>
    private void OnDataChange(
        Subscription subscription, DataChangeNotification notification, IList<string> stringTable)
    {
        var byNode = new Dictionary<string, List<DataValue>>();

        foreach (var item in notification.MonitoredItems)
        {
            if (!_handles.TryGetValue(item.ClientHandle, out var identifier))
            {
                continue;
            }

            if (!byNode.TryGetValue(identifier, out var values))
            {
                byNode[identifier] = values = [];
            }

            values.Add(item.Value);
        }

        lock (_gate)
        {
            if (byNode.TryGetValue(Marker, out var markers) && markers.Count > 0)
            {
                for (var k = 0; k < markers.Count; k++)
                {
                    Emit(byNode, markers[k], k);
                }
            }

            // Carried even when no marker arrived: the values are still in effect for the next one.
            foreach (var (identifier, values) in byNode)
            {
                if (values.Count > 0)
                {
                    _carried[identifier] = values[^1];
                }
            }
        }
    }

    private void Emit(Dictionary<string, List<DataValue>> byNode, DataValue marker, int index)
    {
        var group = new Dictionary<string, DataValue>(Nodes.Length);

        foreach (var (identifier, _) in Nodes)
        {
            // The k-th queued value where the item reported that many; otherwise its last value this
            // cycle; otherwise what it last reported in an earlier cycle, which by the protocol's
            // change semantics is still the value in effect.
            var value = byNode.TryGetValue(identifier, out var values) && values.Count > 0
                ? values[Math.Min(index, values.Count - 1)]
                : _carried.GetValueOrDefault(identifier);

            if (value is null)
            {
                IncompleteGroupsDropped++;
                return;
            }

            group[identifier] = value;
        }

        _ready.Enqueue(Assemble(group, marker));
    }

    private OpcUaFrame Assemble(Dictionary<string, DataValue> group, DataValue sequenceValue)
    {
        var readings = new Dictionary<Channel, double?>();
        var flags = new List<ChannelFlag>();

        foreach (var (identifier, channel) in Nodes)
        {
            if (channel is { } c)
            {
                readings[c] = TranslateStatus(c, group[identifier], flags);
            }
        }

        var sourceTimestamp = sequenceValue.SourceTimestamp;

        return new OpcUaFrame
        {
            Values = readings,
            StatusFlags = flags,
            State = StateFrom(Convert.ToUInt16(group["State"].Value ?? (ushort)0)),
            Sequence = Convert.ToUInt64(sequenceValue.Value ?? 0UL),
            SourceEpochMs = Convert.ToUInt32(group["SourceEpochMs"].Value ?? 0u),
            SourceTimeUtc = new DateTimeOffset(
                sourceTimestamp == default ? DateTime.UtcNow : sourceTimestamp, TimeSpan.Zero),
        };
    }

    /// <summary>§1.4 status translation, identical to the polled path so the two cannot diverge.</summary>
    private static double? TranslateStatus(Channel channel, DataValue value, List<ChannelFlag> flags)
    {
        if (StatusCode.IsBad(value.StatusCode))
        {
            flags.Add(new ChannelFlag(channel, value.StatusCode.Code switch
            {
                StatusCodes.BadNoCommunication => QualityFlag.SensorDisconnected,
                _ => QualityFlag.SensorMissing,
            }));

            return null;
        }

        if (StatusCode.IsUncertain(value.StatusCode))
        {
            flags.Add(new ChannelFlag(channel, QualityFlag.StaleReading));
        }

        return value.Value is null ? null : Convert.ToDouble(value.Value);
    }

    private static EquipmentState StateFrom(ushort code) => code switch
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

    public bool TryDequeue(out OpcUaFrame? frame) => _ready.TryDequeue(out frame);

    public async ValueTask DisposeAsync()
    {
        if (_subscription is not null)
        {
            await _session.RemoveSubscriptionAsync(_subscription, CancellationToken.None);
            _subscription.Dispose();
            _subscription = null;
        }
    }
}
