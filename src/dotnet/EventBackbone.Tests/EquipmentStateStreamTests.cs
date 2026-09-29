using Confluent.Kafka;
using Mair.EdgeGateway;

namespace Mair.EventBackbone.Tests;

/// <summary>
/// The gateway's observed-state stream (<c>EDGE_GATEWAY.md</c> §5.2, <c>OD-008</c>, <c>OD-010</c>).
/// Every record any of these tests produces is validated against <c>equipment-state.schema.json</c>
/// itself, so the rules and the contract are checked together.
/// </summary>
public sealed class EquipmentStateStreamTests : IClassFixture<KafkaBroker>, IAsyncLifetime
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 29, 12, 0, 0, TimeSpan.Zero);
    private const long Epoch = 1_790_000_000_000;

    private static readonly string Root = FindRoot();
    private readonly KafkaBroker _broker;

    public EquipmentStateStreamTests(KafkaBroker broker) => _broker = broker;

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "contracts", "jsonschema", "v1")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("could not find the repository root");
    }

    private static readonly ContractSchema Schema =
        ContractSchema.Load(Path.Combine(Root, "contracts", "jsonschema", "v1", "equipment-state.schema.json"));

    public async Task InitializeAsync()
    {
        using var admin = _broker.Admin();
        await new TopicBootstrap(admin).RunAsync(TopicRegister.All);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static (EquipmentStateStream Stream, RecordingEquipmentStateSink Sink) New(long epoch = Epoch)
    {
        var sink = new RecordingEquipmentStateSink();
        return (new EquipmentStateStream("eq-001", "edge-gateway@gw-1", epoch, sink), sink);
    }

    private static void AssertContract(IEnumerable<EquipmentStateRecord> records)
    {
        foreach (var r in records)
        {
            var verdict = Schema.Validate(EquipmentStateJson.Serialize(r));
            Assert.True(verdict.Valid, $"#{r.StateSequence} {r.State}: {verdict.Reason}");
        }
    }

    private static IEnumerable<(EquipmentState? From, EquipmentState To, string? Id)> Transitions(RecordingEquipmentStateSink sink) =>
        sink.Records.Where(r => !r.IsRefresh).Select(r => (r.PreviousState, r.State, r.TransitionId));

    [Fact]
    public void ALifecycleProducesTheDocumentedTransitionsAndRefreshes()
    {
        var (s, sink) = New();

        s.Tick(T0);                                        // startup: OFFLINE, reported as a refresh
        s.Connected(T0.AddMilliseconds(100));              // T1
        s.Observed(EquipmentState.Running, T0.AddMilliseconds(200));   // T3
        s.Tick(T0.AddSeconds(1));                          // too soon for a refresh
        s.Tick(T0.AddSeconds(2.2));                        // refresh
        s.Observed(EquipmentState.Running, T0.AddSeconds(2.3));        // unchanged: no record
        s.Observed(EquipmentState.Degraded, T0.AddSeconds(3));         // T7
        s.SessionLost(T0.AddSeconds(4));                   // T11

        Assert.Equal(
            [(null, EquipmentState.Connecting, "T1"), (EquipmentState.Connecting, EquipmentState.Running, "T3"),
             (EquipmentState.Running, EquipmentState.Degraded, "T7"), (EquipmentState.Degraded, EquipmentState.Offline, "T11")],
            Transitions(sink).Select(t => (t.From == EquipmentState.Offline && t.Id == "T1" ? null : t.From, t.To, t.Id)));

        var refreshes = sink.Records.Where(r => r.IsRefresh).ToList();
        Assert.Equal(2, refreshes.Count);
        Assert.Equal(EquipmentState.Offline, refreshes[0].State);
        Assert.Equal(EquipmentState.Running, refreshes[1].State);

        // A refresh is a new observation: its time is now, not the time of the last transition.
        // That is what lets gate 10 see a stable machine as fresh (OD-008).
        Assert.Equal(T0.AddSeconds(2.2), refreshes[1].OccurredAtUtc);

        // One counter, contiguous, one epoch; AI eligibility only while RUNNING.
        Assert.Equal(Enumerable.Range(0, sink.Records.Count).Select(i => (ulong)i), sink.Records.Select(r => r.StateSequence));
        Assert.All(sink.Records, r => Assert.Equal(Epoch, r.GatewayEpoch));
        Assert.All(sink.Records, r => Assert.Equal(r.State == EquipmentState.Running, r.AiEligible));

        AssertContract(sink.Records);
    }

    [Theory]
    [InlineData(EquipmentState.Degraded, "T13")]
    [InlineData(EquipmentState.Fault, "T14")]
    [InlineData(EquipmentState.Stopping, "T15")]
    [InlineData(EquipmentState.Idle, "T2")]
    [InlineData(EquipmentState.Running, "T3")]
    public void ConnectingToAMachineAlreadyInAStateUsesItsOwnTransition(EquipmentState found, string id)
    {
        var (s, sink) = New();
        s.Connected(T0);
        s.Observed(found, T0.AddMilliseconds(100));

        Assert.Equal(id, sink.Records[^1].TransitionId);
        AssertContract(sink.Records);
    }

    [Fact]
    public void AConnectTimeoutIsT16_AndLateTelemetryReconnectsThroughConnecting()
    {
        var (s, sink) = New();
        s.Connected(T0);
        s.Tick(T0.AddSeconds(9.9));
        Assert.Equal(EquipmentState.Connecting, s.State);

        s.Tick(T0.AddSeconds(10));
        Assert.Equal(EquipmentState.Offline, s.State);
        Assert.Equal("T16", sink.Records[^1].TransitionId);

        // Telemetry then arrives on the session after all. OFFLINE -> RUNNING is forbidden (§3.2),
        // so the observer reports it the way the model allows: back through CONNECTING.
        s.Observed(EquipmentState.Running, T0.AddSeconds(11));
        Assert.Equal([("T1", EquipmentState.Connecting), ("T3", EquipmentState.Running)],
            sink.Records.TakeLast(2).Select(r => (r.TransitionId, r.State)));
        Assert.DoesNotContain(sink.Records, r => r.PreviousState == EquipmentState.Offline && r.State == EquipmentState.Running);

        AssertContract(sink.Records);
    }

    [Fact]
    public void AChangeThatIsNotOneDocumentedTransitionIsNull_NotGuessed()
    {
        // IDLE straight to DEGRADED: the machine went through RUNNING between two samples and the
        // observer never saw it. No single row of §3.2 describes that (OD-010).
        var (s, sink) = New();
        s.Connected(T0);
        s.Observed(EquipmentState.Idle, T0.AddMilliseconds(100));
        s.Observed(EquipmentState.Degraded, T0.AddMilliseconds(200));

        var last = sink.Records[^1];
        Assert.False(last.IsRefresh);
        Assert.Equal(EquipmentState.Idle, last.PreviousState);
        Assert.Null(last.TransitionId);
        AssertContract(sink.Records);
    }

    [Fact]
    public void EveryDocumentedPairMapsToItsId()
    {
        (EquipmentState, EquipmentState, string)[] table =
        [
            (EquipmentState.Connecting, EquipmentState.Idle, "T2"), (EquipmentState.Connecting, EquipmentState.Running, "T3"),
            (EquipmentState.Idle, EquipmentState.Running, "T4"), (EquipmentState.Running, EquipmentState.Stopping, "T5"),
            (EquipmentState.Stopping, EquipmentState.Idle, "T6"), (EquipmentState.Running, EquipmentState.Degraded, "T7"),
            (EquipmentState.Degraded, EquipmentState.Running, "T8"), (EquipmentState.Running, EquipmentState.Fault, "T9"),
            (EquipmentState.Degraded, EquipmentState.Fault, "T9"), (EquipmentState.Idle, EquipmentState.Fault, "T9"),
            (EquipmentState.Stopping, EquipmentState.Fault, "T9"), (EquipmentState.Fault, EquipmentState.Idle, "T10"),
            (EquipmentState.Degraded, EquipmentState.Stopping, "T12"), (EquipmentState.Connecting, EquipmentState.Degraded, "T13"),
            (EquipmentState.Connecting, EquipmentState.Fault, "T14"), (EquipmentState.Connecting, EquipmentState.Stopping, "T15"),
        ];

        foreach (var (from, to, id) in table)
        {
            Assert.Equal(id, EquipmentStateStream.TransitionIdOf(from, to));
        }

        // And the forbidden ones map to nothing (§3.2).
        Assert.Null(EquipmentStateStream.TransitionIdOf(EquipmentState.Fault, EquipmentState.Running));
        Assert.Null(EquipmentStateStream.TransitionIdOf(EquipmentState.Offline, EquipmentState.Running));
    }

    [Theory]
    [InlineData("eq-1")]
    [InlineData("eq-abc")]
    [InlineData("EQ-001")]
    [InlineData("eq-0000001")]
    public void AnEquipmentIdTheContractRejectsIsRefusedAtConstruction(string id)
    {
        Assert.Throws<ArgumentException>(() =>
            new EquipmentStateStream(id, "edge-gateway@gw-1", Epoch, new RecordingEquipmentStateSink()));
    }

    [Fact]
    public void ARestartedGatewayCannotCollideWithTheRunBeforeIt()
    {
        // stateSequence restarts with the process; the epoch is what keeps the identity unique (OD-008).
        var (before, a) = New(epoch: Epoch);
        var (after, b) = New(epoch: Epoch + 60_000);
        before.Tick(T0);
        after.Tick(T0.AddMinutes(1));

        Assert.Equal(a.Records[0].StateSequence, b.Records[0].StateSequence);   // both 0
        Assert.NotEqual(a.Records[0].GatewayEpoch, b.Records[0].GatewayEpoch);  // and still distinct
    }

    // ------------------------------------------------------------ on a real broker

    [Fact]
    public async Task RecordsAndATombstoneRoundTripThroughTheCompactedTopic()
    {
        // Unique per run, and inside the contract's ^eq-[0-9]{3,6}$ - the first version of this test
        // used hex, and the schema rejected it, which is the schema doing its job.
        var id = "eq-" + Random.Shared.Next(100_000, 999_999);
        using (var sink = new KafkaEquipmentStateSink(_broker.BootstrapServers))
        {
            var s = new EquipmentStateStream(id, "edge-gateway@gw-1", Epoch, sink);
            s.Tick(T0);
            s.Connected(T0.AddMilliseconds(100));
            s.Observed(EquipmentState.Running, T0.AddMilliseconds(200));
            s.Decommission();
            sink.Flush(TimeSpan.FromSeconds(30));
            Assert.Equal(0, sink.DeliveryFailures);
        }

        using var reader = new ConsumerBuilder<string, byte[]?>(new ConsumerConfig
        {
            BootstrapServers = _broker.BootstrapServers,
            GroupId = "states-" + Guid.NewGuid().ToString("N"),
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).Build();
        reader.Subscribe(KafkaEquipmentStateSink.Topic);

        var mine = new List<ConsumeResult<string, byte[]?>>();
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (mine.Count < 4 && DateTime.UtcNow < deadline)
        {
            if (reader.Consume(TimeSpan.FromMilliseconds(500)) is { } r && r.Message.Key == id)
            {
                mine.Add(r);
            }
        }

        Assert.Equal(4, mine.Count);

        // Three records, each valid against the contract, keyed by the equipment...
        foreach (var r in mine.Take(3))
        {
            var verdict = Schema.Validate(r.Message.Value!);
            Assert.True(verdict.Valid, verdict.Reason);
        }

        // ...then the tombstone: same key, no value. It is the only thing that removes a machine
        // from a compact-only topic (OD-008), and it closes ADR-0021 B5's tombstone clause.
        Assert.Null(mine[3].Message.Value);
        Assert.All(mine, r => Assert.Equal(mine[0].Partition, r.Partition)); // one key, one partition, one order
    }
}
