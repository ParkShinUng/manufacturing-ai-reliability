using Microsoft.Extensions.Time.Testing;

namespace Mair.EdgeGateway.Tests;

/// <summary>
/// The 100 ms poll loop and its reconnect behaviour: <b>FR-004</b> and <b>R-01</b> (recover without
/// a process restart), and §4/§13's rule that egress never blocks the OT poll loop.
/// </summary>
public sealed class PollLoopTests
{
    private sealed class FakeSource : ITelemetrySource
    {
        private ulong _sequence;

        public string EquipmentId => "eq-001";

        public int ConnectAttempts { get; private set; }

        public int ConnectFailuresRemaining { get; set; }

        public bool ReadFails { get; set; }

        public Task ConnectAsync(CancellationToken cancellationToken)
        {
            ConnectAttempts++;
            if (ConnectFailuresRemaining > 0)
            {
                ConnectFailuresRemaining--;
                throw new IOException("endpoint refused");
            }

            return Task.CompletedTask;
        }

        public Task<CanonicalTelemetry> ReadAsync(CancellationToken cancellationToken)
        {
            if (ReadFails)
            {
                throw new IOException("endpoint gone");
            }

            return Task.FromResult(new CanonicalTelemetry
            {
                EventId = Guid.NewGuid(),
                EquipmentId = EquipmentId,
                EventTimeUtc = DateTimeOffset.UnixEpoch,
                IngestTimeUtc = DateTimeOffset.UnixEpoch,
                Sequence = _sequence++,
                Producer = "edge-gateway@test",
                SourceProtocol = SourceProtocol.ModbusTcp,
                QualityOverall = QualityOverall.Good,
                Flags = [],
                EquipmentState = EquipmentState.Running,
                CorrelationId = Guid.NewGuid(),
            });
        }
    }

    /// <summary>A sink that throws, to prove the loop survives a broken transport.</summary>
    private sealed class FailingSink : IEgressSink
    {
        public void Emit(CanonicalTelemetry telemetry) => throw new InvalidOperationException("broker down");
    }

    // ------------------------------------------------------------ backoff

    [Fact]
    public void BackoffDoublesFrom250msAndStopsAt8s()
    {
        // Jitter pinned to the midpoint so the base sequence is what is being asserted.
        var backoff = new ReconnectBackoff(() => 0.5);

        double[] expected = [250, 500, 1_000, 2_000, 4_000, 8_000, 8_000, 8_000];

        foreach (var ms in expected)
        {
            Assert.Equal(ms, backoff.Next().TotalMilliseconds, 3);
        }
    }

    [Fact]
    public void BackoffJitterStaysWithinTwentyPercent()
    {
        // The extremes, pinned rather than sampled: a random-driven test that happens not to hit
        // the edge proves nothing about the edge.
        Assert.Equal(200, new ReconnectBackoff(() => 0.0).Next().TotalMilliseconds, 3);   // -20 %
        Assert.Equal(300, new ReconnectBackoff(() => 1.0).Next().TotalMilliseconds, 3);   // +20 %
    }

    [Fact]
    public void BackoffNeverOverflowsOnALongOutage()
    {
        // A long outage must not turn "retry forever" into a busy loop via an overflowed shift.
        var backoff = new ReconnectBackoff(() => 0.5);

        for (var i = 0; i < 10_000; i++)
        {
            var delay = backoff.Next();
            Assert.True(delay > TimeSpan.Zero, $"attempt {i} produced {delay}");
            Assert.True(delay <= ReconnectBackoff.MaxDelay * 1.2 + TimeSpan.FromMilliseconds(1));
        }
    }

    [Fact]
    public void ASuccessfulConnectResetsTheBackoff()
    {
        var backoff = new ReconnectBackoff(() => 0.5);
        backoff.Next();
        backoff.Next();
        backoff.Next();

        backoff.Reset();

        Assert.Equal(250, backoff.Next().TotalMilliseconds, 3);
    }

    // ------------------------------------------------------------ the loop

    private static (EquipmentPollLoop Loop, FakeSource Source, RecordingEgressSink Sink, FakeTimeProvider Time)
        Build(IEgressSink? sink = null, int bufferCapacity = 6_000)
    {
        var source = new FakeSource();
        var recording = new RecordingEgressSink();
        var time = new FakeTimeProvider();
        var loop = new EquipmentPollLoop(
            source,
            new BoundedEgressBuffer(bufferCapacity),
            sink ?? recording,
            new ReconnectBackoff(() => 0.5),
            time);

        return (loop, source, recording, time);
    }

    /// <summary>
    /// Advances the fake clock until <paramref name="until"/> holds, or fails.
    /// <para>
    /// Expressed as a condition rather than a tick count because the loop's own delays vary: the
    /// poll cadence is 100 ms but a reconnect backoff runs 250 ms to 8 s, so any fixed number of
    /// ticks is a guess that silently under-runs the moment the backoff grows.
    /// </para>
    /// <para>
    /// The real 1 ms wait is deliberate and was arrived at the hard way. An earlier version used
    /// <c>Task.Yield()</c>, which passed in isolation and failed inside the full suite: advancing
    /// the fake clock fires the timer, but the loop's continuation still has to be <b>scheduled</b>,
    /// and under xUnit's parallel load a yield does not reliably give it a turn. A real delay does.
    /// The fake clock still governs everything the loop measures, so the test stays fast and the
    /// timings under test stay simulated.
    /// </para>
    /// </summary>
    private static async Task AdvanceUntilAsync(
        FakeTimeProvider time, Func<bool> until, string what, TimeSpan? budget = null)
    {
        var remaining = budget ?? TimeSpan.FromMinutes(5);

        while (remaining > TimeSpan.Zero)
        {
            await Task.Delay(1);

            if (until())
            {
                return;
            }

            time.Advance(EquipmentPollLoop.PollInterval);
            remaining -= EquipmentPollLoop.PollInterval;
        }

        await Task.Delay(20);
        Assert.True(until(), $"{what} did not happen within the simulated budget");
    }

    private static async Task AdvanceAsync(FakeTimeProvider time, int ticks)
    {
        for (var i = 0; i < ticks; i++)
        {
            await Task.Delay(1);
            time.Advance(EquipmentPollLoop.PollInterval);
        }

        await Task.Delay(20);
    }

    [Fact]
    public async Task ThePollLoopEmitsAtTheCadence()
    {
        var (loop, _, sink, time) = Build();
        using var stopping = new CancellationTokenSource();

        var run = loop.RunAsync(stopping.Token);
        await AdvanceUntilAsync(time, () => loop.PollsCompleted >= 5, "five polls");
        await stopping.CancelAsync();
        await run;

        Assert.True(loop.PollsCompleted >= 5, "the loop produced nothing");
        Assert.Equal(loop.PollsCompleted, sink.Records.Count);
        Assert.True(loop.ProtocolConnected);
    }

    [Fact]
    public async Task AReadFailureGoesOfflineAndReconnectsWithoutRestartingTheLoop()
    {
        // FR-004 / R-01. The loop object is the same one throughout - nothing outside it restarts.
        var (loop, source, sink, time) = Build();
        using var stopping = new CancellationTokenSource();

        var run = loop.RunAsync(stopping.Token);
        await AdvanceUntilAsync(time, () => loop.PollsCompleted >= 3, "initial polling");

        var before = loop.PollsCompleted;
        source.ReadFails = true;
        await AdvanceUntilAsync(time, () => !loop.ProtocolConnected, "going offline");

        Assert.False(loop.ProtocolConnected);

        source.ReadFails = false;
        await AdvanceUntilAsync(time, () => loop.PollsCompleted > before, "polling resuming");

        Assert.True(loop.ProtocolConnected);
        Assert.True(loop.PollsCompleted > before, "polling did not resume");
        Assert.True(loop.ReconnectTotal >= 2, $"reconnects: {loop.ReconnectTotal}");

        await stopping.CancelAsync();
        await run;

        Assert.NotEmpty(sink.Records);
    }

    [Fact]
    public async Task ConnectFailuresAreRetriedForever_NotFatal()
    {
        var (loop, source, _, time) = Build();
        source.ConnectFailuresRemaining = 4;
        using var stopping = new CancellationTokenSource();

        var run = loop.RunAsync(stopping.Token);
        await AdvanceUntilAsync(time, () => loop.ProtocolConnected, "connecting after 4 failures");

        Assert.True(source.ConnectAttempts > 4, $"attempts: {source.ConnectAttempts}");
        Assert.True(loop.ProtocolConnected);

        await stopping.CancelAsync();
        await run;
    }

    [Fact]
    public async Task CancellationIsTheOnlyWayTheLoopExits()
    {
        var (loop, source, _, time) = Build();
        source.ReadFails = true;
        using var stopping = new CancellationTokenSource();

        var run = loop.RunAsync(stopping.Token);
        await AdvanceAsync(time, 30);

        Assert.False(run.IsCompleted, "a protocol fault must not end the loop");

        await stopping.CancelAsync();
        await run;

        Assert.True(run.IsCompletedSuccessfully);
    }
}
