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
/// publishing cycle still receives what it missed (OD-005).
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

    private readonly ISession _session;
    private readonly string _equipmentId;
    private readonly ushort _namespaceIndex;
    private readonly ConcurrentQueue<OpcUaFrame> _ready = new();
    private readonly OpcUaSampleAssembler _assembler = new();
    private readonly Lock _gate = new();

    // Written once, before the subscription exists on the server, and only read afterwards. The
    // first publish can arrive before CreateAsync returns, so filling this after it would drop the
    // first notifications and race a Dictionary write against the callback (COD-P2-002).
    private readonly Dictionary<uint, string> _handles = [];

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
    /// Markers dropped because not every node delivered a value with the marker's own timestamp —
    /// at start-up, or after a queue overflow discarded one. Counted rather than filled in: inventing
    /// a value is the fabricated reading ADR-0018 forbids, and the missing sequence then surfaces
    /// downstream as <c>SEQUENCE_GAP</c>.
    /// </summary>
    public long IncompleteGroupsDropped
    {
        get { lock (_gate) { return _assembler.IncompleteGroupsDropped; } }
    }

    /// <summary>Values that arrived after their marker had already been emitted or dropped.</summary>
    public long LateValuesDiscarded
    {
        get { lock (_gate) { return _assembler.LateValuesDiscarded; } }
    }

    /// <summary>
    /// True while the server is publishing. False once the keep-alive has lapsed, which is how a
    /// dead endpoint becomes visible on a push path that otherwise just goes quiet.
    /// </summary>
    public bool IsPublishing => _subscription is { PublishingStopped: false } && _session.Connected;

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
            FastDataChangeCallback = OnDataChange,
        };

        foreach (var identifier in OpcUaFrame.NodeIdentifiers)
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

                // No deadband (§1.3): it would suppress the unchanged values SENSOR_FROZEN needs.
                //
                // The trigger includes the timestamp, so every node reports on every model step even
                // when its value did not change. That is what makes the source timestamp an exact
                // coherence key: a sample is the ten values stamped with one instant, and nothing has
                // to be inferred about a node that stayed silent (COD-P2-001).
                Filter = new DataChangeFilter
                {
                    Trigger = DataChangeTrigger.StatusValueTimestamp,
                    DeadbandType = (uint)DeadbandType.None,
                    DeadbandValue = 0,
                },
            };

            _handles[item.ClientHandle] = identifier;
            subscription.AddItem(item);
        }

        _session.AddSubscription(subscription);
        await subscription.CreateAsync(cancellationToken);
        _subscription = subscription;
    }

    private void OnDataChange(
        Subscription subscription, DataChangeNotification notification, IList<string> stringTable)
    {
        var values = new List<(string Node, DataValue Value)>(notification.MonitoredItems.Count);

        foreach (var item in notification.MonitoredItems)
        {
            if (_handles.TryGetValue(item.ClientHandle, out var identifier))
            {
                values.Add((identifier, item.Value));
            }
        }

        lock (_gate)
        {
            foreach (var group in _assembler.AddCycle(values))
            {
                _ready.Enqueue(OpcUaFrame.From(group));
            }
        }
    }

    public bool TryDequeue(out OpcUaFrame? frame) => _ready.TryDequeue(out frame);

    public async ValueTask DisposeAsync()
    {
        if (_subscription is not null)
        {
            try
            {
                await _session.RemoveSubscriptionAsync(_subscription, CancellationToken.None);
            }
            catch (ServiceResultException)
            {
                // The session is already gone, which is the usual reason to be disposing it.
            }

            _subscription.Dispose();
            _subscription = null;
        }
    }
}

/// <summary>
/// Turns per-cycle monitored-item values into coherent samples, keyed on the source timestamp.
/// <para>
/// The simulator stamps all ten nodes of one model step with one instant, and the subscription's
/// trigger makes every node report on every step. So a sample is <b>exactly</b> the ten values that
/// share the marker's timestamp. This replaced a k-th-value pairing that assumed every item carried
/// the same number of queued values per cycle; OPC UA guarantees no such thing, and when discard-
/// oldest drops different values from different items it stitched readings from different steps.
/// </para>
/// <para>
/// A node's value for a step can land one publishing cycle after the marker — the server samples
/// items independently, so a step can straddle a cycle boundary — so an incomplete marker is held
/// for <see cref="HoldCycles"/> cycles before it is dropped. Complete markers are emitted at once;
/// the hold costs latency only on a step that is actually straddling.
/// </para>
/// </summary>
internal sealed class OpcUaSampleAssembler
{
    private const string Marker = "SequenceNo";

    /// <summary>Straddling needs one; two leaves a margin for a late sampling pass.</summary>
    internal const int HoldCycles = 2;

    /// <summary>
    /// Values kept per node, a backstop against a marker that never arrives. Two cycles of a full
    /// queue is 20; anything this far behind can no longer complete.
    /// </summary>
    private const int MaxValuesPerNode = 64;

    private readonly Dictionary<string, Dictionary<DateTime, DataValue>> _byNode = [];
    private readonly List<(DataValue Value, int Age)> _markers = [];
    private DateTime _settledThrough = DateTime.MinValue;

    public long IncompleteGroupsDropped { get; private set; }

    public long LateValuesDiscarded { get; private set; }

    /// <summary>Adds one publishing cycle and returns the samples it completed, oldest first.</summary>
    public IReadOnlyList<DataValue[]> AddCycle(IEnumerable<(string Node, DataValue Value)> values)
    {
        foreach (var (node, value) in values)
        {
            var timestamp = value.SourceTimestamp;

            if (timestamp <= _settledThrough)
            {
                // Its marker has already been emitted or dropped. Using it now would attach it to a
                // later sample; the count makes a server whose steps straddle more than the hold
                // visible rather than silently lossy.
                LateValuesDiscarded++;
                continue;
            }

            if (node == Marker)
            {
                // A marker with a bad status carries no sequence: the equipment is not answering, and
                // that is reported by the session and the next sequence, not by a sample of nulls.
                if (!StatusCode.IsBad(value.StatusCode) && timestamp != DateTime.MinValue)
                {
                    _markers.Add((value, 0));
                }

                continue;
            }

            if (!_byNode.TryGetValue(node, out var byTime))
            {
                _byNode[node] = byTime = [];
            }

            byTime[timestamp] = value;

            if (byTime.Count > MaxValuesPerNode)
            {
                byTime.Remove(byTime.Keys.Min());
            }
        }

        _markers.Sort((a, b) => a.Value.SourceTimestamp.CompareTo(b.Value.SourceTimestamp));

        var completed = new List<DataValue[]>();
        var i = 0;

        for (; i < _markers.Count; i++)
        {
            var (marker, age) = _markers[i];
            var group = GroupAt(marker);

            if (group is not null)
            {
                completed.Add(group);
            }
            else if (age >= HoldCycles)
            {
                IncompleteGroupsDropped++;
            }
            else
            {
                break; // hold this one, and everything after it, so samples leave in order
            }

            Settle(marker.SourceTimestamp);
        }

        _markers.RemoveRange(0, i);
        for (var j = 0; j < _markers.Count; j++)
        {
            _markers[j] = (_markers[j].Value, _markers[j].Age + 1);
        }

        return completed;
    }

    private DataValue[]? GroupAt(DataValue marker)
    {
        var timestamp = marker.SourceTimestamp;
        var group = new DataValue[OpcUaFrame.NodeIdentifiers.Length];

        for (var k = 0; k < group.Length; k++)
        {
            var node = OpcUaFrame.NodeIdentifiers[k];

            if (node == Marker)
            {
                group[k] = marker;
            }
            else if (_byNode.TryGetValue(node, out var byTime) && byTime.TryGetValue(timestamp, out var value))
            {
                group[k] = value;
            }
            else
            {
                return null;
            }
        }

        return group;
    }

    private void Settle(DateTime timestamp)
    {
        _settledThrough = timestamp;

        foreach (var byTime in _byNode.Values)
        {
            foreach (var stale in byTime.Keys.Where(t => t <= timestamp).ToList())
            {
                byTime.Remove(stale);
            }
        }
    }
}
