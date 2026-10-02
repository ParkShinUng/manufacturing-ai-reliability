using System.Diagnostics;
using System.Text;
using Confluent.Kafka;

namespace Mair.EventBackbone.Tests;

/// <summary>
/// <b>KAFKA-004 / AC-027</b> — a dependency outage stalls the shared consumer on the record that
/// failed, in order, and loses nothing (OD-022), on a real broker.
/// </summary>
[Collection(SharedKafka.Name)]
public sealed class DependencyOutageTests(KafkaBroker broker)
{
    private static readonly string Root = FindRoot();

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

    private static byte[] Healthy() => File.ReadAllBytes(Path.Combine(Root, "contracts", "examples", "telemetry.healthy.json"));

    private async Task<TopicSpec> TopicAsync(int partitions)
    {
        var spec = new TopicSpec($"kafka004.{Guid.NewGuid().ToString("N")[..8]}.v1", partitions, CleanupPolicy.Delete, TimeSpan.FromHours(1));
        using var admin = broker.Admin();
        await new TopicBootstrap(admin).RunAsync([spec, spec.DeadLetter()]);
        return spec;
    }

    private IProducer<string, byte[]> Producer() =>
        new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = broker.BootstrapServers, Acks = Acks.All }).Build();

    private ConsumerConfig Group(string? id = null) => new()
    {
        BootstrapServers = broker.BootstrapServers,
        GroupId = id ?? "kafka004-" + Guid.NewGuid().ToString("N"),
        AutoOffsetReset = AutoOffsetReset.Earliest,
    };

    private static async Task<TopicPartitionOffset> ProduceAsync(IProducer<string, byte[]> producer, string topic, int partition, string tag)
    {
        var result = await producer.ProduceAsync(new TopicPartition(topic, partition), new Message<string, byte[]>
        {
            Key = tag,
            Value = Healthy(),
            Headers = [new Header("tag", Encoding.UTF8.GetBytes(tag))],
        });
        return result.TopicPartitionOffset;
    }

    private static string Tag(ConsumeResult<string, byte[]> r) => Encoding.UTF8.GetString(r.Message.Headers.GetLastBytes("tag"));

    private long Committed(string group, TopicPartition partition)
    {
        using var admin = broker.Admin();
        var offsets = admin.ListConsumerGroupOffsetsAsync([new ConsumerGroupTopicPartitions(group, [partition])]).GetAwaiter().GetResult();
        return offsets.Single().Partitions.Single().Offset.Value;
    }

    private int DeadLetters(string topic)
    {
        using var reader = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = broker.BootstrapServers,
            GroupId = "reader-" + Guid.NewGuid().ToString("N"),
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).Build();
        reader.Subscribe(topic + ".dlq");
        var count = 0;
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            if (reader.Consume(TimeSpan.FromMilliseconds(500)) is not null)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Settles records on a background loop until stopped, as RunAsync would, without its caught-up check.</summary>
    private static (Task Loop, CancellationTokenSource Stop) Run(ContractConsumer consumer)
    {
        var stop = new CancellationTokenSource();
        var loop = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                await consumer.ProcessOneAsync(TimeSpan.FromMilliseconds(200), stop.Token);
            }
        });
        return (loop, stop);
    }

    private static async Task Until(Func<bool> condition, string what, int seconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, what);
            await Task.Delay(50);
        }
    }

    private static async Task StopAsync((Task Loop, CancellationTokenSource Stop) run)
    {
        await run.Stop.CancelAsync();
        try
        {
            await run.Loop;
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Fact]
    public async Task AnOutageStallsEverythingOnTheFailedRecord_InOrder_WithBackoff_AndLosesNothing()
    {
        var spec = await TopicAsync(partitions: 2);
        using var producer = Producer();
        var n = await ProduceAsync(producer, spec.Name, 0, "N");
        await ProduceAsync(producer, spec.Name, 0, "N+1");
        await ProduceAsync(producer, spec.Name, 0, "N+2");
        await ProduceAsync(producer, spec.Name, 1, "other");

        var group = Group();
        var down = true;
        var handled = new List<string>();
        var attemptsAt = new List<long>();
        using var consumer = new ContractConsumer(group, spec.Name, Schema(), producer,
            (r, _) =>
            {
                if (Tag(r) == "N")
                {
                    attemptsAt.Add(Stopwatch.GetTimestamp());
                    if (Volatile.Read(ref down))
                    {
                        throw new DependencyUnavailableException("store unavailable");
                    }
                }

                lock (handled)
                {
                    handled.Add(Tag(r));
                }

                return Task.CompletedTask;
            },
            jitter: () => 0.5, // the midpoint: the base schedule is what is asserted
            spec: spec);

        var run = Run(consumer);
        try
        {
            await Until(() => consumer.Outage is not null, "the outage was never reported");
            var outage = consumer.Outage!;
            Assert.Equal(n.TopicPartition, outage.At.TopicPartition);
            Assert.Equal(n.Offset, outage.At.Offset);
            Assert.Equal(nameof(DependencyUnavailableException), outage.ExceptionType);
            Assert.False(consumer.CaughtUp);

            // Four attempts: 1 s, 2 s and 4 s apart. Nothing else is handled meanwhile, on either
            // partition, and nothing on N's partition is committed past it.
            int before;
            lock (handled)
            {
                before = handled.Count;
            }

            await Until(() => attemptsAt.Count >= 4, "fewer than four attempts during the outage", seconds: 30);
            lock (handled)
            {
                Assert.Equal(before, handled.Count);
                Assert.DoesNotContain("N+1", handled);
            }

            Assert.True(Committed(group.GroupId, n.TopicPartition) <= n.Offset.Value);
            var gaps = attemptsAt.Zip(attemptsAt.Skip(1), (a, b) => Stopwatch.GetElapsedTime(a, b).TotalSeconds).Take(3).ToList();
            Assert.InRange(gaps[0], 0.9, 1.6);
            Assert.InRange(gaps[1], 1.9, 2.6);
            Assert.InRange(gaps[2], 3.9, 4.6);

            Volatile.Write(ref down, false);
            await Until(() => { lock (handled) { return handled.Count == 4; } }, "not every record was handled after the outage");
        }
        finally
        {
            await StopAsync(run);
        }

        Assert.Null(consumer.Outage);
        var onZero = handled.Where(t => t.StartsWith('N')).ToList();
        Assert.Equal(["N", "N+1", "N+2"], onZero);
        Assert.Equal(n.Offset.Value + 3, Committed(group.GroupId, n.TopicPartition));
        Assert.Equal(0, consumer.DeadLettered);
        Assert.Equal(0, DeadLetters(spec.Name));
    }

    [Fact]
    public async Task AFailureOfAnotherKindAfterTheOutageStillDeadLettersAfterThreeAttempts()
    {
        var spec = await TopicAsync(partitions: 1);
        using var producer = Producer();
        await ProduceAsync(producer, spec.Name, 0, "N");

        var calls = 0;
        using var consumer = new ContractConsumer(Group(), spec.Name, Schema(), producer,
            (_, _) => ++calls <= 2
                ? throw new DependencyUnavailableException("store unavailable")
                : throw new InvalidOperationException("a defect in the handler"),
            delay: (_, _) => Task.CompletedTask,
            jitter: () => 0.5,
            spec: spec);

        var run = Run(consumer);
        try
        {
            await Until(() => consumer.DeadLettered == 1, "the record was not dead-lettered", seconds: 60);
        }
        finally
        {
            await StopAsync(run);
        }

        Assert.Equal(1, consumer.DeadLetteredByReason[RejectionClass.HandlerFailed]);
        Assert.True(consumer.OutageRetries >= 1);
    }

    [Fact]
    public async Task WhenTheConsumerGoesAwayDuringAnOutage_TheRecordIsNotCommitted_AndTheNextOwnerReceivesIt()
    {
        var spec = await TopicAsync(partitions: 1);
        using var producer = Producer();
        var n = await ProduceAsync(producer, spec.Name, 0, "N");
        var group = Group();

        var first = new ContractConsumer(group, spec.Name, Schema(), producer,
            (_, _) => throw new DependencyUnavailableException("store unavailable"), spec: spec);
        var run = Run(first);
        await Until(() => first.Outage is not null, "the outage was never reported");
        await StopAsync(run);
        first.Dispose();

        Assert.True(Committed(group.GroupId, n.TopicPartition) < 0 || Committed(group.GroupId, n.TopicPartition) <= n.Offset.Value);

        var received = new List<string>();
        using var next = new ContractConsumer(group, spec.Name, Schema(), producer,
            (r, _) => { received.Add(Tag(r)); return Task.CompletedTask; }, spec: spec);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (received.Count == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "the next owner never received the held record");
            await next.ProcessOneAsync(TimeSpan.FromMilliseconds(500));
        }

        Assert.Equal(["N"], received);
        Assert.Equal(0, DeadLetters(spec.Name));
    }
}
