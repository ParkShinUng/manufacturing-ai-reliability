using System.Text;
using Confluent.Kafka;

namespace Mair.EventBackbone.Tests;

/// <summary>
/// Lag and record-age, the Phase 3 deliverable Codex found missing (<c>P3-COD-002</c>), measured
/// against <c>KAFKA_TOPOLOGY_AND_SEMANTICS.md</c> §12. As in Phase 2, the numbers are counted here
/// and exported in Phase 8; what this proves is that the counts are right.
/// </summary>
[Collection(SharedKafka.Name)]
public sealed class ConsumerMetricsTests
{
    private static readonly string Root = FindRoot();
    private readonly KafkaBroker _broker;

    public ConsumerMetricsTests(KafkaBroker broker) => _broker = broker;

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

    private static byte[] Healthy() =>
        File.ReadAllBytes(Path.Combine(Root, "contracts", "examples", "telemetry.healthy.json"));

    /// <summary>A one-partition topic of its own, with its DLQ, so nothing another test wrote is counted.</summary>
    private async Task<string> TopicAsync()
    {
        var spec = new TopicSpec("metrics.test." + Guid.NewGuid().ToString("N")[..8] + ".v1", 1, CleanupPolicy.Delete, TimeSpan.FromHours(1));
        using var admin = _broker.Admin();
        await new TopicBootstrap(admin).RunAsync([spec, spec.DeadLetter()]);
        return spec.Name;
    }

    private IProducer<string, byte[]> Producer() =>
        new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = _broker.BootstrapServers, Acks = Acks.All }).Build();

    private ContractConsumer Consumer(string topic, IProducer<string, byte[]> deadLetters) =>
        new(new ConsumerConfig { BootstrapServers = _broker.BootstrapServers, GroupId = "metrics-" + Guid.NewGuid().ToString("N"), AutoOffsetReset = AutoOffsetReset.Earliest },
            topic, Schema(), deadLetters, (_, _) => Task.CompletedTask);

    private static async Task SettleAsync(ContractConsumer consumer, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (consumer.Processed < count)
        {
            Assert.True(DateTime.UtcNow < deadline, $"settled {consumer.Processed} of {count}");
            await consumer.ProcessOneAsync(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task LagIsTheRecordsBetweenTheNewestSettledAndTheHighWatermark()
    {
        var topic = await TopicAsync();
        using var producer = Producer();
        for (var i = 0; i < 10; i++)
        {
            await producer.ProduceAsync(topic, new Message<string, byte[]> { Key = "eq-001", Value = Healthy() });
        }

        using var consumer = Consumer(topic, producer);
        await SettleAsync(consumer, 3);

        // Three of ten settled: seven behind.
        var lag = Assert.Single(consumer.Lag);
        Assert.Equal(7, lag.Value);

        await SettleAsync(consumer, 10);
        Assert.Equal(0, consumer.Lag.Single().Value);
    }

    [Fact]
    public async Task RecordAgeIsMeasuredFromTheRecordNotFromWhenItWasRead()
    {
        // §12 makes record age the primary signal, so it must mean the age of the data. A record
        // stamped a minute ago and read now is a minute old, however quickly the consumer read it.
        var topic = await TopicAsync();
        using var producer = Producer();
        var stamped = DateTime.UtcNow.AddMinutes(-1);
        await producer.ProduceAsync(topic, new Message<string, byte[]>
        {
            Key = "eq-001",
            Value = Healthy(),
            Timestamp = new Timestamp(stamped),
        });

        using var consumer = Consumer(topic, producer);
        await SettleAsync(consumer, 1);

        var age = Assert.Single(consumer.RecordAge()).Value;
        Assert.InRange(age, TimeSpan.FromSeconds(59), TimeSpan.FromSeconds(90));
    }

    [Fact]
    public async Task DeadLettersAreCountedByReason()
    {
        var topic = await TopicAsync();
        using var producer = Producer();
        var invalid = Encoding.UTF8.GetString(Healthy()).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2");
        await producer.ProduceAsync(topic, new Message<string, byte[]> { Key = "eq-001", Value = Encoding.UTF8.GetBytes(invalid) });
        await producer.ProduceAsync(topic, new Message<string, byte[]> { Key = "eq-001", Value = "{ not json"u8.ToArray() });
        await producer.ProduceAsync(topic, new Message<string, byte[]> { Key = "eq-001", Value = Healthy() });

        using var consumer = Consumer(topic, producer);
        await SettleAsync(consumer, 3);

        Assert.Equal(1, consumer.DeadLetteredByReason[RejectionClass.SchemaInvalid]);
        Assert.Equal(1, consumer.DeadLetteredByReason[RejectionClass.Unparseable]);
        Assert.False(consumer.DeadLetteredByReason.ContainsKey(RejectionClass.HandlerFailed));
        Assert.Equal(2, consumer.DeadLettered);
    }
}
