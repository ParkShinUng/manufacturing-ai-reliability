using System.Diagnostics;
using System.Text;
using Confluent.Kafka;
using Mair.EdgeGateway;

namespace Mair.EventBackbone.Tests;

/// <summary>
/// <b>FAIL-KAFKA-001 / AC-045</b> — broker restart and replay <b>mechanics</b>, on a real broker.
/// <para>
/// Mechanics only, by design (OD-007): the read models a replay rebuilds are Phase 4 (<c>AC-046</c>),
/// and that a replay moves no equipment is Phase 6 (<c>AC-003</c>). What Phase 3 can prove, and
/// proves here, is that a replay-eligible group can be rewound over a known range and reads exactly
/// that range, that the Supervisor's group cannot be rewound, and that the Supervisor's start-up
/// seek discards a backlog even when committed offsets say otherwise.
/// </para>
/// </summary>
public sealed class ReplayTests : IClassFixture<KafkaBroker>, IAsyncLifetime
{
    private const string Topic = "factory.telemetry.v1";

    private static readonly string Root = FindRoot();
    private readonly KafkaBroker _broker;

    public ReplayTests(KafkaBroker broker) => _broker = broker;

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

    private static ContractSchema Schema() =>
        ContractSchema.Load(Path.Combine(Root, "contracts", "jsonschema", "v1", "telemetry.schema.json"));

    private static string Healthy() =>
        File.ReadAllText(Path.Combine(Root, "contracts", "examples", "telemetry.healthy.json"));

    public async Task InitializeAsync()
    {
        using var admin = _broker.Admin();
        await new TopicBootstrap(admin).RunAsync(TopicRegister.All);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private IProducer<string, byte[]> Producer() =>
        new ProducerBuilder<string, byte[]>(new ProducerConfig
        {
            BootstrapServers = _broker.BootstrapServers,
            EnableIdempotence = true,
            Acks = Acks.All,
        }).Build();

    private ConsumerConfig Group(string group) => new()
    {
        BootstrapServers = _broker.BootstrapServers,
        GroupId = group,
        AutoOffsetReset = AutoOffsetReset.Earliest,
    };

    /// <summary>
    /// Ten records under one key — one partition, one order — each carrying its position in
    /// <c>x-n</c>, so "exactly this range" can be checked by content, not only by offset.
    /// </summary>
    private async Task<List<TopicPartitionOffset>> ProduceTenAsync(string key)
    {
        using var producer = Producer();
        var offsets = new List<TopicPartitionOffset>();
        for (var n = 0; n < 10; n++)
        {
            var r = await producer.ProduceAsync(Topic, new Message<string, byte[]>
            {
                Key = key,
                Value = Encoding.UTF8.GetBytes(Healthy()),
                Headers = [new Header("x-n", Encoding.UTF8.GetBytes(n.ToString()))],
            });
            offsets.Add(r.TopicPartitionOffset);
        }

        return offsets;
    }

    private static string N(ConsumeResult<string, byte[]> r) => Encoding.UTF8.GetString(r.Message.Headers.GetLastBytes("x-n"));

    private async Task<List<ConsumeResult<string, byte[]>>> DrainAsync(
        string group, string key, int expected, bool seekToEnd = false, Func<Task>? afterFirstPoll = null)
    {
        var seen = new List<ConsumeResult<string, byte[]>>();
        using var deadLetters = Producer();
        using var consumer = new ContractConsumer(Group(group), Topic, Schema(), deadLetters,
            (r, _) => { if (r.Message.Key == key) { seen.Add(r); } return Task.CompletedTask; },
            seekToEndOnAssignment: seekToEnd);

        var deadline = DateTime.UtcNow.AddSeconds(30);
        var first = true;
        while (seen.Count < expected && DateTime.UtcNow < deadline)
        {
            await consumer.ProcessOneAsync(TimeSpan.FromSeconds(1));
            if (first && afterFirstPoll is not null)
            {
                first = false;
                await afterFirstPoll();
            }
        }

        // A little longer, so an extra record would be caught rather than missed.
        var settle = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < settle)
        {
            await consumer.ProcessOneAsync(TimeSpan.FromMilliseconds(200));
        }

        return seen;
    }

    [Fact]
    public async Task AReplayEligibleGroupRewoundOverARangeReadsExactlyThatRange_InOrder()
    {
        var key = "eq-" + Random.Shared.Next(100_000, 999_999);
        var produced = await ProduceTenAsync(key);
        var group = ConsumerGroupRegister.OperationsProjector;

        // The group reads everything and commits past it.
        var firstPass = await DrainAsync(group, key, expected: 10);
        Assert.Equal(Enumerable.Range(0, 10).Select(i => i.ToString()), firstPass.Select(N));

        // Rewind to record 4. The group has no members now, which Kafka requires for the rewind.
        using (var admin = _broker.Admin())
        {
            await ReplayTool.RewindAsync(admin, group, [produced[4]]);
        }

        var replay = await DrainAsync(group, key, expected: 6);

        // Exactly 4..9: nothing before the range, nothing missing, nothing twice, in order.
        Assert.Equal(Enumerable.Range(4, 6).Select(i => i.ToString()), replay.Select(N));
        Assert.Equal(produced.Skip(4).Select(p => p.Offset), replay.Select(r => r.Offset));

        // And the group's committed position is back past the end: offsets committed after
        // processing, so the replayed records are settled, not left to be read a third time.
        using var probe = new ConsumerBuilder<string, byte[]>(Group(group)).Build();
        var committed = probe.Committed([produced[^1].TopicPartition], TimeSpan.FromSeconds(10)).Single();
        Assert.Equal(produced[^1].Offset.Value + 1, committed.Offset.Value);
    }

    [Fact]
    public async Task TheSafetySupervisorsGroupCannotBeRewound_AndIsLeftUntouched()
    {
        var key = "eq-" + Random.Shared.Next(100_000, 999_999);
        var produced = await ProduceTenAsync(key);
        var group = ConsumerGroupRegister.SafetySupervisor;
        await DrainAsync(group, key, expected: 10);

        using var admin = _broker.Admin();
        var refused = await Assert.ThrowsAsync<ReplayRefusedException>(
            () => ReplayTool.RewindAsync(admin, group, [produced[0]]));

        Assert.Contains("DEC-002", refused.Message);

        // Refused, not half-done: the committed offset has not moved.
        using var probe = new ConsumerBuilder<string, byte[]>(Group(group)).Build();
        var committed = probe.Committed([produced[^1].TopicPartition], TimeSpan.FromSeconds(10)).Single();
        Assert.Equal(produced[^1].Offset.Value + 1, committed.Offset.Value);
    }

    [Theory]
    [InlineData("cg.nobody-registered-this.v1")]
    [InlineData("cg.operations-projector.v2")]
    public async Task AGroupNobodyRegisteredIsRefused(string group)
    {
        using var admin = _broker.Admin();
        await Assert.ThrowsAsync<ReplayRefusedException>(
            () => ReplayTool.RewindAsync(admin, group, [new TopicPartitionOffset(Topic, 0, 0)]));
    }

    [Fact]
    public async Task SeekToEndOnAssignmentDiscardsABacklog_EvenWithCommittedOffsetsBehindIt()
    {
        // §6.1. Committed offsets sit at the start of a ten-record backlog - the position a Supervisor
        // restarting after downtime would find. auto.offset.reset=latest would not help: it applies
        // only to a group with no committed offset. The seek on assignment must discard the backlog.
        var key = "eq-" + Random.Shared.Next(100_000, 999_999);
        var produced = await ProduceTenAsync(key);
        var group = "cg.seek-to-end-proof." + Guid.NewGuid().ToString("N")[..8];

        using (var admin = _broker.Admin())
        {
            await admin.AlterConsumerGroupOffsetsAsync(
                [new ConsumerGroupTopicPartitionOffsets(group, [produced[0]])]);
        }

        var seen = await DrainAsync(group, key, expected: 1, seekToEnd: true, afterFirstPoll: async () =>
        {
            // One fresh record after the consumer has its assignment.
            using var producer = Producer();
            await producer.ProduceAsync(Topic, new Message<string, byte[]>
            {
                Key = key,
                Value = Encoding.UTF8.GetBytes(Healthy()),
                Headers = [new Header("x-n", "fresh"u8.ToArray())],
            });
        });

        // Only the fresh one. Not one of the ten backlog records was delivered.
        Assert.Equal(["fresh"], seen.Select(N));
    }

    [Fact]
    public async Task ABrokerRestartIsAbsorbed_AndConsumersResumeWithin30Seconds()
    {
        // F06/F07: the gateway keeps emitting through the outage; the client queues and retries
        // within §11's 30 s delivery budget, so a short restart loses nothing, and a consumer
        // picks the records up once the broker is back.
        var equipmentId = "eq-" + Random.Shared.Next(100_000, 999_999);
        var records = Enumerable.Range(0, 20).Select(i => new CanonicalTelemetry
        {
            EventId = Guid.NewGuid(),
            EquipmentId = equipmentId,
            EventTimeUtc = DateTimeOffset.UtcNow,
            IngestTimeUtc = DateTimeOffset.UtcNow,
            Sequence = (ulong)i,
            Producer = "edge-gateway@fail-kafka-001",
            SourceProtocol = SourceProtocol.OpcUa,
            QualityOverall = QualityOverall.Good,
            Flags = [],
            TemperatureC = 50, VibrationRms = 2, CurrentA = 12, VoltageV = 400, Rpm = 1780, TorqueNm = 42, OperationRatePct = 100,
            EquipmentState = EquipmentState.Running,
            CorrelationId = Guid.NewGuid(),
        }).ToList();

        using var sink = new KafkaTelemetrySink(_broker.BootstrapServers);
        foreach (var r in records.Take(10))
        {
            sink.Emit(r);
        }

        sink.Flush(TimeSpan.FromSeconds(10));

        // Down for five seconds, emitting throughout; Emit must still not block (§4).
        var restart = _broker.RestartAsync(TimeSpan.FromSeconds(5));
        var started = Stopwatch.GetTimestamp();
        foreach (var r in records.Skip(10))
        {
            sink.Emit(r);
        }

        Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(500), "Emit blocked during the outage");
        await restart;
        var back = Stopwatch.GetTimestamp();

        sink.Flush(TimeSpan.FromSeconds(30));
        Assert.Equal(20, sink.Delivered);
        Assert.Equal(0, sink.DeliveryFailures);

        // A consumer reads all twenty after the restart, within §F06's 30 s.
        var seen = new HashSet<ulong>();
        using var deadLetters = Producer();
        using var consumer = new ContractConsumer(Group("cg.restart-proof." + Guid.NewGuid().ToString("N")[..8]), Topic, Schema(), deadLetters,
            (r, _) =>
            {
                if (r.Message.Key == equipmentId)
                {
                    seen.Add(System.Text.Json.JsonDocument.Parse(r.Message.Value).RootElement.GetProperty("sequence"u8).GetUInt64());
                }

                return Task.CompletedTask;
            });

        while (seen.Count < 20)
        {
            Assert.True(Stopwatch.GetElapsedTime(back) < TimeSpan.FromSeconds(30), $"resumed with {seen.Count} of 20 after 30 s");
            await consumer.ProcessOneAsync(TimeSpan.FromSeconds(1));
        }

        Assert.Equal(Enumerable.Range(0, 20).Select(i => (ulong)i), seen.Order());
    }
}
