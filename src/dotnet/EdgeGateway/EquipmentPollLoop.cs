namespace Mair.EdgeGateway;

/// <summary>
/// A protocol-agnostic source of canonical telemetry for one equipment. The Modbus and OPC UA
/// clients each have an adapter, so the poll loop does not branch on protocol.
/// </summary>
public interface ITelemetrySource
{
    string EquipmentId { get; }

    Task ConnectAsync(CancellationToken cancellationToken);

    Task<CanonicalTelemetry> ReadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Polls one equipment at the 100 ms cadence, normalises, and hands the result to the egress
/// buffer (`EDGE_GATEWAY.md` §10, §11).
/// <para>
/// <b>The loop never blocks on egress.</b> Kafka is classified non-control-critical and "never
/// blocks the OT poll loop" (§4), so a slow or dead sink degrades into dropped events, never into
/// missed samples. That asymmetry is the whole reason the buffer exists.
/// </para>
/// <para>
/// Reconnect is infinite and needs no process restart (FR-004, R-01). A read failure moves the
/// equipment to <c>OFFLINE</c> and starts the backoff; it does not tear down the loop, because a
/// gateway that exits on a cable fault is a gateway that needs an operator to restore telemetry.
/// </para>
/// </summary>
public sealed class EquipmentPollLoop
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly ITelemetrySource _source;
    private readonly BoundedEgressBuffer _buffer;
    private readonly IEgressSink _sink;
    private readonly ReconnectBackoff _backoff;
    private readonly TimeProvider _time;

    public EquipmentPollLoop(
        ITelemetrySource source,
        BoundedEgressBuffer buffer,
        IEgressSink sink,
        ReconnectBackoff? backoff = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(sink);

        _source = source;
        _buffer = buffer;
        _sink = sink;
        _backoff = backoff ?? new ReconnectBackoff();
        _time = timeProvider ?? TimeProvider.System;
    }

    public bool ProtocolConnected { get; private set; }

    public long PollsCompleted { get; private set; }

    public long ReconnectTotal { get; private set; }

    /// <summary>Loop deadline overruns. Counted, never silently skipped (`EQUIPMENT_SIMULATOR.md` §12).</summary>
    public long LoopOverruns { get; private set; }

    /// <summary>
    /// Runs until cancelled. Exits only on cancellation — every other failure is a protocol fault
    /// to recover from, not a reason to stop polling.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!ProtocolConnected && !await TryConnectAsync(cancellationToken))
            {
                continue;
            }

            var started = _time.GetTimestamp();

            try
            {
                var telemetry = await _source.ReadAsync(cancellationToken);
                _buffer.Enqueue(telemetry);
                Drain();
                PollsCompleted++;

                // Reset here, not on connect. An endpoint that accepts connections but fails every
                // read would otherwise reset the backoff on each attempt and spin at full CPU -
                // connecting successfully is not the same as working.
                _backoff.Reset();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // Any protocol failure: the endpoint is gone, stale, or refusing. The equipment is
                // OFFLINE until a read succeeds; the loop itself keeps running.
                //
                // The backoff applies here too. Without it a half-dead endpoint - one whose TCP
                // accept works while its reads fail - becomes a hot loop: connect, fail, connect,
                // fail, with nothing between the attempts.
                ProtocolConnected = false;
                await Delay(_backoff.Next(), cancellationToken);
                continue;
            }

            await WaitForNextTickAsync(started, cancellationToken);
        }
    }

    private async Task<bool> TryConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _source.ConnectAsync(cancellationToken);
            ProtocolConnected = true;
            ReconnectTotal++;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception)
        {
            await Delay(_backoff.Next(), cancellationToken);
            return false;
        }
    }

    private void Drain()
    {
        // Draining here rather than on a second thread keeps the ordering guarantee simple: the
        // buffer is written and read by the same loop, so there is no interleaving to reason about.
        while (_buffer.TryDequeue(out var telemetry) && telemetry is not null)
        {
            _sink.Emit(telemetry);
        }
    }

    private async Task WaitForNextTickAsync(long started, CancellationToken cancellationToken)
    {
        var elapsed = _time.GetElapsedTime(started);
        var remaining = PollInterval - elapsed;

        if (remaining <= TimeSpan.Zero)
        {
            // The cycle took longer than its own period. Counted rather than absorbed: a gateway
            // quietly polling at 150 ms would look healthy while silently halving its sample rate.
            LoopOverruns++;
            return;
        }

        await Delay(remaining, cancellationToken);
    }

    private async Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, _time, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is the documented way to stop the loop.
        }
    }
}

/// <summary>Adapts the Modbus client to <see cref="ITelemetrySource"/>.</summary>
public sealed class ModbusTelemetrySource(
    string equipmentId,
    int equipmentIndex,
    ModbusTelemetryClient client,
    TelemetryNormaliser normaliser) : ITelemetrySource
{
    public string EquipmentId { get; } = equipmentId;

    public Task ConnectAsync(CancellationToken cancellationToken) => client.ConnectAsync(cancellationToken);

    public async Task<CanonicalTelemetry> ReadAsync(CancellationToken cancellationToken)
        => normaliser.Normalise(await client.ReadAsync(equipmentIndex, cancellationToken));
}

/// <summary>Adapts the OPC UA client to <see cref="ITelemetrySource"/>.</summary>
public sealed class OpcUaTelemetrySource(
    string equipmentId,
    OpcUaTelemetryClient client,
    TelemetryNormaliser normaliser) : ITelemetrySource
{
    public string EquipmentId { get; } = equipmentId;

    public Task ConnectAsync(CancellationToken cancellationToken) => client.ConnectAsync(cancellationToken);

    public async Task<CanonicalTelemetry> ReadAsync(CancellationToken cancellationToken)
    {
        var frame = await client.ReadAsync(EquipmentId, cancellationToken);
        return normaliser.Normalise(
            frame.Values, frame.Sequence, frame.SourceEpochMs, frame.State, frame.SourceTimeUtc);
    }
}
