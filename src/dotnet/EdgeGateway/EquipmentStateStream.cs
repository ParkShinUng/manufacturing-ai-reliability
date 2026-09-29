namespace Mair.EdgeGateway;

/// <summary>
/// One record for <c>factory.equipment-states.v1</c>, shaped by <c>equipment-state.schema.json</c>.
/// <para>
/// <see cref="PreviousState"/> and <see cref="TransitionId"/> are both absent on a <b>refresh</b>,
/// which reports that nothing transitioned (<c>EDGE_GATEWAY.md</c> §5.2). On a transition,
/// <see cref="TransitionId"/> may still be <c>null</c>, meaning the observed change is not one
/// documented transition (<c>OD-010</c>) — which is why "is a refresh" is its own flag rather than
/// inferred from a null.
/// </para>
/// </summary>
public sealed record EquipmentStateRecord
{
    public required Guid EventId { get; init; }

    public required string EquipmentId { get; init; }

    public required EquipmentState State { get; init; }

    public required bool IsRefresh { get; init; }

    public EquipmentState? PreviousState { get; init; }

    public string? TransitionId { get; init; }

    public required ulong StateSequence { get; init; }

    public required long GatewayEpoch { get; init; }

    public required string ObservedBy { get; init; }

    public bool AiEligible => State == EquipmentState.Running;

    /// <summary>The observation time — for a refresh too, which is what gate 10 measures (OD-008).</summary>
    public required DateTimeOffset OccurredAtUtc { get; init; }

    public required DateTimeOffset IngestTimeUtc { get; init; }

    public required Guid CorrelationId { get; init; }
}

/// <summary>Where equipment-state records go. The Kafka binding and a recording one for tests.</summary>
public interface IEquipmentStateSink
{
    void Emit(EquipmentStateRecord record);

    /// <summary>A null value under the equipment's key: it has left the configured inventory (OD-008).</summary>
    void Tombstone(string equipmentId);
}

public sealed class RecordingEquipmentStateSink : IEquipmentStateSink
{
    private readonly List<EquipmentStateRecord> _records = [];
    private readonly List<string> _tombstones = [];

    public IReadOnlyList<EquipmentStateRecord> Records => _records;

    public IReadOnlyList<string> Tombstones => _tombstones;

    public void Emit(EquipmentStateRecord record) => _records.Add(record);

    public void Tombstone(string equipmentId) => _tombstones.Add(equipmentId);
}

/// <summary>
/// The gateway's <b>observed</b> state for one equipment, and the records that report it
/// (<c>EDGE_GATEWAY.md</c> §5.2, <c>OD-008</c>, <c>OD-010</c>).
/// <para>
/// Observed, not ground truth: a gateway that cannot reach a healthy machine reports
/// <c>OFFLINE</c> while the machine is <c>RUNNING</c>, and says so through <c>observedBy</c>.
/// </para>
/// <para>
/// A record goes out on every observed transition, and a refresh every
/// <see cref="RefreshInterval"/> otherwise, so the Safety Supervisor's gate 10 — which fails on
/// state older than 10 s — always has a recent observation of a machine that is simply staying
/// put. The interval's constraint, not its value, is the contract:
/// <c>refresh + worst-case latency + skew budget &lt; 10 s</c>.
/// </para>
/// </summary>
public sealed partial class EquipmentStateStream
{
    [System.Text.RegularExpressions.GeneratedRegex("^eq-[0-9]{3,6}$")]
    private static partial System.Text.RegularExpressions.Regex EquipmentIdPattern();

    public static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    /// <summary><c>connect_timeout</c>: connected but no valid telemetry for this long is T16.</summary>
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    private readonly string _equipmentId;
    private readonly string _observedBy;
    private readonly long _gatewayEpoch;
    private readonly IEquipmentStateSink _sink;

    private EquipmentState _state = EquipmentState.Offline;
    private ulong _nextSequence;
    private DateTimeOffset? _lastEmitted;
    private DateTimeOffset? _connectingSince;

    /// <param name="gatewayEpoch">
    /// Milliseconds since the Unix epoch at gateway process start, shared by every stream the
    /// process owns. It is what makes a restarted <c>stateSequence</c> safe (OD-008).
    /// </param>
    public EquipmentStateStream(string equipmentId, string observedBy, long gatewayEpoch, IEquipmentStateSink sink)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(equipmentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(observedBy);

        // The schema's own pattern. A stream built for an id the contract rejects would emit an
        // invalid record every two seconds forever; refusing it here is one failure at start-up.
        if (!EquipmentIdPattern().IsMatch(equipmentId))
        {
            throw new ArgumentException(
                $"'{equipmentId}' does not match equipment-state.schema.json's equipmentId pattern ^eq-[0-9]{{3,6}}$",
                nameof(equipmentId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(gatewayEpoch);
        ArgumentNullException.ThrowIfNull(sink);

        _equipmentId = equipmentId;
        _observedBy = observedBy;
        _gatewayEpoch = gatewayEpoch;
        _sink = sink;
    }

    public EquipmentState State => _state;

    /// <summary>The session came up. Nothing valid has been read yet, so the machine is CONNECTING (T1).</summary>
    public void Connected(DateTimeOffset now)
    {
        _connectingSince = now;
        Transition(EquipmentState.Connecting, now, "T1");
    }

    /// <summary>The session was lost, from whatever state (T11).</summary>
    public void SessionLost(DateTimeOffset now)
    {
        _connectingSince = null;
        Transition(EquipmentState.Offline, now, "T11");
    }

    /// <summary>A valid telemetry sample reported the equipment's own state.</summary>
    public void Observed(EquipmentState reported, DateTimeOffset now)
    {
        if (_state == EquipmentState.Offline)
        {
            // Telemetry after a connect timeout: the session had not actually gone. Reconnection
            // is still reported through CONNECTING, because OFFLINE -> RUNNING is forbidden and an
            // observer must not publish a transition the model says cannot happen.
            Transition(EquipmentState.Connecting, now, "T1");
        }

        _connectingSince = null;
        Transition(reported, now, TransitionIdOf(_state, reported));
    }

    /// <summary>Called on the poll cadence: the connect timeout, and the refresh.</summary>
    public void Tick(DateTimeOffset now)
    {
        if (_state == EquipmentState.Connecting && _connectingSince is { } since && now - since >= ConnectTimeout)
        {
            _connectingSince = null;
            Transition(EquipmentState.Offline, now, "T16");
            return;
        }

        if (_lastEmitted is null || now - _lastEmitted.Value >= RefreshInterval)
        {
            Emit(now, previous: null, transitionId: null, isRefresh: true);
        }
    }

    /// <summary>The equipment left the configured inventory. Compaction keeps a record forever otherwise.</summary>
    public void Decommission() => _sink.Tombstone(_equipmentId);

    private void Transition(EquipmentState next, DateTimeOffset now, string? transitionId)
    {
        if (next == _state)
        {
            return; // not a change; the refresh reports it
        }

        var previous = _state;
        _state = next;
        Emit(now, previous, transitionId, isRefresh: false);
    }

    private void Emit(DateTimeOffset now, EquipmentState? previous, string? transitionId, bool isRefresh)
    {
        _sink.Emit(new EquipmentStateRecord
        {
            EventId = Guid.NewGuid(),
            EquipmentId = _equipmentId,
            State = _state,
            IsRefresh = isRefresh,
            PreviousState = previous,
            TransitionId = transitionId,
            StateSequence = _nextSequence++,
            GatewayEpoch = _gatewayEpoch,
            ObservedBy = _observedBy,
            OccurredAtUtc = now,
            IngestTimeUtc = now,
            CorrelationId = Guid.NewGuid(),
        });

        _lastEmitted = now;
    }

    /// <summary>
    /// <c>EQUIPMENT_MODEL_AND_STATE.md</c> §3.2 for a change the equipment itself reported. The
    /// OFFLINE ones are not here: T11 and T16 are the same pair with different triggers, and the
    /// caller knows which one happened.
    /// </summary>
    public static string? TransitionIdOf(EquipmentState from, EquipmentState to) => (from, to) switch
    {
        (EquipmentState.Connecting, EquipmentState.Idle) => "T2",
        (EquipmentState.Connecting, EquipmentState.Running) => "T3",
        (EquipmentState.Idle, EquipmentState.Running) => "T4",
        (EquipmentState.Running, EquipmentState.Stopping) => "T5",
        (EquipmentState.Stopping, EquipmentState.Idle) => "T6",
        (EquipmentState.Running, EquipmentState.Degraded) => "T7",
        (EquipmentState.Degraded, EquipmentState.Running) => "T8",
        (EquipmentState.Running or EquipmentState.Degraded or EquipmentState.Idle or EquipmentState.Stopping,
            EquipmentState.Fault) => "T9",
        (EquipmentState.Fault, EquipmentState.Idle) => "T10",
        (EquipmentState.Degraded, EquipmentState.Stopping) => "T12",
        (EquipmentState.Connecting, EquipmentState.Degraded) => "T13",
        (EquipmentState.Connecting, EquipmentState.Fault) => "T14",
        (EquipmentState.Connecting, EquipmentState.Stopping) => "T15",

        // Not one documented transition: the observer missed an intermediate state between two
        // samples (OD-010). Reported as such rather than guessed.
        _ => null,
    };
}
