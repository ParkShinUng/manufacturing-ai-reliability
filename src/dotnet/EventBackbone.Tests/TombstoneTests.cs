using System.Text;
using Confluent.Kafka;

namespace Mair.EventBackbone.Tests;

/// <summary>
/// <b>KAFKA-003 / AC-027</b> — a tombstone is a record only where the contract says so (OD-021), on
/// a real broker, through the production <see cref="ContractConsumer"/>.
/// <para>
/// Each test gets topics of its own, one that allows tombstones and one that does not, so the only
/// difference between the two paths is the register entry.
/// </para>
/// </summary>
[Collection(SharedKafka.Name)]
public sealed class TombstoneTests(KafkaBroker broker)
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
        ContractSchema.Load(Path.Combine(Root, "contracts", "jsonschema", "v1", "equipment-state.schema.json"));

    private async Task<TopicSpec> TopicAsync(bool allowsTombstones)
    {
        var spec = new TopicSpec($"kafka003.{(allowsTombstones ? "allow" : "deny")}.{Guid.NewGuid().ToString("N")[..8]}.v1",
            1, CleanupPolicy.Compact, null, allowsTombstones);
        using var admin = broker.Admin();
        await new TopicBootstrap(admin).RunAsync([spec, spec.DeadLetter()]);
        return spec;
    }

    private IProducer<string, byte[]> Producer() =>
        new ProducerBuilder<string, byte[]>(new ProducerConfig { BootstrapServers = broker.BootstrapServers, Acks = Acks.All }).Build();

    private ConsumerConfig Group() => new()
    {
        BootstrapServers = broker.BootstrapServers,
        GroupId = "kafka003-" + Guid.NewGuid().ToString("N"),
        AutoOffsetReset = AutoOffsetReset.Earliest,
    };

    private static async Task<TopicPartitionOffset> TombstoneAsync(IProducer<string, byte[]> producer, string topic) =>
        (await producer.ProduceAsync(topic, new Message<string, byte[]> { Key = "eq-001", Value = null! })).TopicPartitionOffset;

    private List<ConsumeResult<string, byte[]>> DeadLetters(string topic)
    {
        using var reader = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = broker.BootstrapServers,
            GroupId = "reader-" + Guid.NewGuid().ToString("N"),
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).Build();
        reader.Subscribe(topic + ".dlq");
        var found = new List<ConsumeResult<string, byte[]>>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (reader.Consume(TimeSpan.FromMilliseconds(500)) is { } r)
            {
                found.Add(r);
            }
        }

        return found;
    }

    private static string Header(ConsumeResult<string, byte[]> r, string name) => Encoding.UTF8.GetString(r.Message.Headers.GetLastBytes(name));

    private long Committed(string group, TopicPartitionOffset at)
    {
        using var admin = broker.Admin();
        var offsets = admin.ListConsumerGroupOffsetsAsync([new ConsumerGroupTopicPartitions(group, [at.TopicPartition])]).GetAwaiter().GetResult();
        return offsets.Single().Partitions.Single().Offset.Value;
    }

    [Fact]
    public async Task OnATopicThatAllowsTombstones_TheTombstoneHandlerRuns_AndTheOffsetCommitsAfterIt()
    {
        var spec = await TopicAsync(allowsTombstones: true);
        using var producer = Producer();
        var at = await TombstoneAsync(producer, spec.Name);

        var group = Group();
        var tombstones = new List<ConsumeResult<string, byte[]>>();
        var handled = 0;
        using (var consumer = new ContractConsumer(group, spec.Name, Schema(), producer,
                   (_, _) => { handled++; return Task.CompletedTask; },
                   tombstoneHandler: (r, _) => { tombstones.Add(r); return Task.CompletedTask; },
                   spec: spec))
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (consumer.Processed < 1)
            {
                Assert.True(DateTime.UtcNow < deadline, "the tombstone was not consumed");
                await consumer.ProcessOneAsync(TimeSpan.FromSeconds(1));
            }

            Assert.Equal(0, consumer.DeadLettered);
        }

        var tombstone = Assert.Single(tombstones);
        Assert.Null(tombstone.Message.Value);
        Assert.Equal("eq-001", tombstone.Message.Key);
        Assert.Equal(0, handled);
        Assert.Equal(at.Offset.Value + 1, Committed(group.GroupId, at));
    }

    [Fact]
    public async Task OnATopicThatAllowsNone_ANullValueIsDeadLetteredOnTheFirstAttempt_WithTheFullHeaderSet()
    {
        var spec = await TopicAsync(allowsTombstones: false);
        using var producer = Producer();
        await TombstoneAsync(producer, spec.Name);

        var handled = 0;
        using (var consumer = new ContractConsumer(Group(), spec.Name, Schema(), producer,
                   (_, _) => { handled++; return Task.CompletedTask; }, spec: spec))
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (consumer.Processed < 1)
            {
                Assert.True(DateTime.UtcNow < deadline, "the null value was not consumed");
                await consumer.ProcessOneAsync(TimeSpan.FromSeconds(1));
            }
        }

        Assert.Equal(0, handled);
        var dead = Assert.Single(DeadLetters(spec.Name));
        Assert.Null(dead.Message.Value);
        Assert.Equal("Unparseable", Header(dead, "x-dlq-error-class"));
        Assert.Equal("1", Header(dead, "x-dlq-attempt-count"));
        Assert.Contains("tombstone", Header(dead, "x-dlq-reason"));
        foreach (var required in new[] { "x-dlq-source-topic", "x-dlq-source-partition", "x-dlq-source-offset", "x-dlq-first-failed-at" })
        {
            Assert.False(string.IsNullOrEmpty(Header(dead, required)), required);
        }
    }

    [Fact]
    public async Task AFailingTombstoneHandlerIsRetriedOnTheSection9Schedule_ThenDeadLettered()
    {
        var spec = await TopicAsync(allowsTombstones: true);
        using var producer = Producer();
        await TombstoneAsync(producer, spec.Name);

        var attempts = 0;
        var waits = new List<TimeSpan>();
        using (var consumer = new ContractConsumer(Group(), spec.Name, Schema(), producer,
                   (_, _) => Task.CompletedTask,
                   delay: (wait, _) => { waits.Add(wait); return Task.CompletedTask; },
                   jitter: () => 0.5,
                   tombstoneHandler: (_, _) => { attempts++; throw new InvalidOperationException("projection unavailable"); },
                   spec: spec))
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (consumer.Processed < 1)
            {
                Assert.True(DateTime.UtcNow < deadline, "the tombstone was not settled");
                await consumer.ProcessOneAsync(TimeSpan.FromSeconds(1));
            }
        }

        Assert.Equal(ContractConsumer.MaxAttempts, attempts);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], waits);
        var dead = Assert.Single(DeadLetters(spec.Name));
        Assert.Equal("HandlerFailed", Header(dead, "x-dlq-error-class"));
        Assert.Equal("3", Header(dead, "x-dlq-attempt-count"));
    }

    [Fact]
    public void ATombstoneHandlerForATopicThatAllowsNone_OrNoneForOneThatDoes_FailsConstruction()
    {
        using var producer = Producer();
        var deny = new TopicSpec("kafka003.construct.deny.v1", 1, CleanupPolicy.Delete, TimeSpan.FromHours(1));
        var allow = deny with { Name = "kafka003.construct.allow.v1", AllowsTombstones = true };

        Assert.Throws<ArgumentException>(() => new ContractConsumer(Group(), deny.Name, Schema(), producer,
            (_, _) => Task.CompletedTask, tombstoneHandler: (_, _) => Task.CompletedTask, spec: deny));
        Assert.Throws<ArgumentException>(() => new ContractConsumer(Group(), allow.Name, Schema(), producer,
            (_, _) => Task.CompletedTask, spec: allow));
    }

    [Fact]
    public void TheRegisterAllowsTombstonesOnTheEquipmentStateTopicAndNowhereElse()
    {
        Assert.Equal(["factory.equipment-states.v1"], TopicRegister.All.Where(t => t.AllowsTombstones).Select(t => t.Name));
    }
}
