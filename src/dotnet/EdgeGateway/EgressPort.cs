namespace Mair.EdgeGateway;

/// <summary>
/// Where canonical telemetry leaves the gateway.
/// <para>
/// This interface is the <b>Phase 2 / Phase 3 boundary</b> (`EDGE_GATEWAY.md` §5.1). It is not an
/// abstraction introduced for testing: §4 classifies Kafka as <b>non-control-critical</b> and says
/// it "never blocks the OT poll loop", and §13 says blocking is forbidden. A bounded buffer between
/// the poll loop and the transport is what makes that true, and a buffer has two ends. Naming the
/// far end is implementing the design, not inventing structure.
/// </para>
/// <para>
/// Phase 2 proves the gateway <b>emits</b> canonical records (AC-001, AC-021, AC-022, AC-023).
/// Phase 3 binds this port to <c>factory.telemetry.v1</c> under the topology contract (FR-010).
/// </para>
/// </summary>
public interface IEgressSink
{
    /// <summary>
    /// Accepts one record. Implementations must not block the caller: the caller is the OT poll
    /// loop, and a slow transport must degrade into dropped events, never into missed samples.
    /// </summary>
    void Emit(CanonicalTelemetry telemetry);
}

/// <summary>
/// Bounded egress buffer: capacity 6 000 (~30 s at demo scale), <b>drop oldest</b>, never blocking
/// (`EDGE_GATEWAY.md` §13).
/// <para>
/// Drop-oldest rather than drop-newest because stale telemetry is rejected by the safety gates
/// anyway, so the newest sample is always the more useful one. Buffering beyond the freshness
/// horizon buys nothing.
/// </para>
/// <para>
/// Every drop is counted and reported: the next emitted event carries
/// <see cref="QualityFlag.BufferOverflowDrop"/>, so bounded loss is <b>visible</b> loss (D-02).
/// A silent drop would let the analytics backbone lose data without anyone knowing.
/// </para>
/// </summary>
public sealed class BoundedEgressBuffer
{
    public const int DefaultCapacity = 6_000;

    private readonly Queue<CanonicalTelemetry> _queue = new();
    private readonly int _capacity;
    private readonly Action? _onDrop;

    public BoundedEgressBuffer(int capacity = DefaultCapacity, Action? onDrop = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _capacity = capacity;
        _onDrop = onDrop;
    }

    public int Depth => _queue.Count;

    public int Capacity => _capacity;

    public long DroppedTotal { get; private set; }

    /// <summary>Enqueues, dropping the oldest record if full. Never blocks, never throws on a full buffer.</summary>
    public void Enqueue(CanonicalTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);

        if (_queue.Count >= _capacity)
        {
            _queue.Dequeue();
            DroppedTotal++;
            _onDrop?.Invoke();
        }

        _queue.Enqueue(telemetry);
    }

    public bool TryDequeue(out CanonicalTelemetry? telemetry) => _queue.TryDequeue(out telemetry);

    public IReadOnlyList<CanonicalTelemetry> DrainAll()
    {
        var drained = _queue.ToArray();
        _queue.Clear();
        return drained;
    }
}

/// <summary>
/// An egress sink that keeps everything it is given. This is the Phase 2 proving ground for
/// AC-001, AC-021, AC-022 and AC-023 — the gateway's normalisation is what those criteria are
/// about, and none of them maps to FR-010.
/// </summary>
public sealed class RecordingEgressSink : IEgressSink
{
    private readonly List<CanonicalTelemetry> _records = [];

    public IReadOnlyList<CanonicalTelemetry> Records => _records;

    public void Emit(CanonicalTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);
        _records.Add(telemetry);
    }
}
