using System.Text;
using Confluent.Kafka;

namespace Mair.EventBackbone.Tests;

/// <summary>
/// <b>KAFKA-002 / AC-027</b> — a schema-invalid record reaches the DLQ on the <b>first</b> attempt,
/// through the production consume path (<c>OD-009</c>), on a real broker.
/// <para>
/// What is asserted is where each record ends up and when its offset is committed, because those
/// are the two things that decide whether data can be lost or duplicated.
/// </para>
/// </summary>
public sealed class ContractConsumerTests : IClassFixture<KafkaBroker>, IAsyncLifetime
{
    private const string Topic = "factory.telemetry.v1";

    private static readonly string Root = FindRoot();

    private readonly KafkaBroker _broker;

    public ContractConsumerTests(KafkaBroker broker) => _broker = broker;

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

    private static ContractSchema Telemetry() =>
        ContractSchema.Load(Path.Combine(Root, "contracts", "jsonschema", "v1", "telemetry.schema.json"));

    private static string Healthy() =>
        File.ReadAllText(Path.Combine(Root, "contracts", "examples", "telemetry.healthy.json"));

    public async Task InitializeAsync()
    {
        // The register, as the bootstrap creates it. Idempotent, so every test class can call it.
        using var admin = _broker.Admin();
        await new TopicBootstrap(admin).RunAsync(TopicRegister.All);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private IProducer<string, byte[]> Producer(int messageTimeoutMs = 30_000) =>
        new ProducerBuilder<string, byte[]>(new ProducerConfig
        {
            BootstrapServers = _broker.BootstrapServers,
            EnableIdempotence = true,
            Acks = Acks.All,
            MessageTimeoutMs = messageTimeoutMs,
        }).Build();

    private ConsumerConfig ConsumerConfig(string group) => new()
    {
        BootstrapServers = _broker.BootstrapServers,
        GroupId = group,
        AutoOffsetReset = AutoOffsetReset.Earliest,
    };

    /// <summary>A unique key per test, so records from one test never land in another's view.</summary>
    private static string Key() => "eq-" + Guid.NewGuid().ToString("N")[..8];

    private async Task<TopicPartitionOffset> ProduceAsync(IProducer<string, byte[]> producer, string topic, string key, string json)
    {
        var result = await producer.ProduceAsync(topic, new Message<string, byte[]>
        {
            Key = key,
            Value = Encoding.UTF8.GetBytes(json),
            Headers = [new Header("x-origin", "test"u8.ToArray())],
        });

        return result.TopicPartitionOffset;
    }

    /// <summary>Every record on a DLQ carrying <paramref name="key"/>.</summary>
    private List<ConsumeResult<string, byte[]>> ReadDeadLetters(string key)
    {
        using var reader = new ConsumerBuilder<string, byte[]>(new ConsumerConfig
        {
            BootstrapServers = _broker.BootstrapServers,
            GroupId = "reader-" + Guid.NewGuid().ToString("N"),
            AutoOffsetReset = AutoOffsetReset.Earliest,
            EnableAutoCommit = false,
        }).Build();

        reader.Subscribe(Topic + ".dlq");
        var found = new List<ConsumeResult<string, byte[]>>();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var r = reader.Consume(TimeSpan.FromMilliseconds(500));
            if (r is not null && r.Message.Key == key)
            {
                found.Add(r);
            }
        }

        return found;
    }

    private static string Header(ConsumeResult<string, byte[]> r, string name) =>
        Encoding.UTF8.GetString(r.Message.Headers.GetLastBytes(name));

    // ------------------------------------------------------------ KAFKA-002

    [Fact]
    public async Task ASchemaInvalidRecordIsDeadLetteredOnTheFirstAttempt_AndTheValidOneAfterItIsHandled()
    {
        var key = Key();
        var invalid = Healthy().Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2");
        using var producer = Producer();
        var invalidAt = await ProduceAsync(producer, Topic, key, invalid);
        var validAt = await ProduceAsync(producer, Topic, key, Healthy());

        var handled = new List<ConsumeResult<string, byte[]>>();
        var consumer = new ContractConsumer(ConsumerConfig("kafka002-" + key), Topic, Telemetry(), producer,
            (r, _) =>
            {
                if (r.Message.Key == key)
                {
                    handled.Add(r);
                }

                return Task.CompletedTask;
            });

        using (consumer)
        {
            await SettleUntilAsync(consumer, () => handled.Count == 1);
        }

        // The downstream handler never saw the invalid record, and did see the valid one.
        var only = Assert.Single(handled);
        Assert.Equal(validAt.Offset, only.Offset);

        // Exactly one DLQ record for this key: the invalid one, never the valid one.
        var dead = Assert.Single(ReadDeadLetters(key));
        Assert.Equal(Encoding.UTF8.GetBytes(invalid), dead.Message.Value); // original bytes, untouched

        // §9's seven headers.
        Assert.Contains("telemetry.schema.json", Header(dead, "x-dlq-reason"));
        Assert.Equal(Topic, Header(dead, "x-dlq-source-topic"));
        Assert.Equal(invalidAt.Partition.Value.ToString(), Header(dead, "x-dlq-source-partition"));
        Assert.Equal(invalidAt.Offset.Value.ToString(), Header(dead, "x-dlq-source-offset"));
        Assert.Equal("1", Header(dead, "x-dlq-attempt-count")); // first attempt, no retry
        Assert.True(DateTimeOffset.TryParse(Header(dead, "x-dlq-first-failed-at"), out _));
        Assert.Equal("SchemaInvalid", Header(dead, "x-dlq-error-class"));

        // The source record's own headers survive onto the DLQ record.
        Assert.Equal("test", Header(dead, "x-origin"));
    }

    [Fact]
    public async Task OffsetsAreCommittedOnlyAfterTheRecordIsSettled()
    {
        var key = Key();
        using var producer = Producer();
        await ProduceAsync(producer, Topic, key, "{ not json");
        var last = await ProduceAsync(producer, Topic, key, Healthy());

        var group = "commit-" + key;
        var sawValid = false;
        using (var consumer = new ContractConsumer(ConsumerConfig(group), Topic, Telemetry(), producer,
                   (r, _) => { sawValid |= r.Message.Key == key; return Task.CompletedTask; }))
        {
            await SettleUntilAsync(consumer, () => sawValid);
        }

        // The group's committed position is past both records: the unparseable one was settled on
        // the DLQ and the valid one was handled, so neither will be redelivered.
        using var probe = new ConsumerBuilder<string, byte[]>(ConsumerConfig(group)).Build();
        var committed = probe.Committed([last.TopicPartition], TimeSpan.FromSeconds(10)).Single();
        Assert.Equal(last.Offset.Value + 1, committed.Offset.Value);

        var dead = Assert.Single(ReadDeadLetters(key));
        Assert.Equal("Unparseable", Header(dead, "x-dlq-error-class"));
    }

    [Fact]
    public async Task AValidRecordNeverReachesTheDeadLetterQueue()
    {
        // The required negative case: a consumer that DLQ'd everything would pass the others.
        var key = Key();
        using var producer = Producer();
        await ProduceAsync(producer, Topic, key, Healthy());

        var handled = 0;
        using (var consumer = new ContractConsumer(ConsumerConfig("valid-" + key), Topic, Telemetry(), producer,
                   (r, _) => { handled += r.Message.Key == key ? 1 : 0; return Task.CompletedTask; }))
        {
            await SettleUntilAsync(consumer, () => handled == 1);
        }

        Assert.Empty(ReadDeadLetters(key));
    }

    [Fact]
    public async Task AHandlerFailureIsRetriedWithTheSection9Backoff_ThenDeadLettered()
    {
        var key = Key();
        using var producer = Producer();
        await ProduceAsync(producer, Topic, key, Healthy());

        var attempts = 0;
        var waits = new List<TimeSpan>();
        using (var consumer = new ContractConsumer(ConsumerConfig("retry-" + key), Topic, Telemetry(), producer,
                   (r, _) =>
                   {
                       if (r.Message.Key != key)
                       {
                           return Task.CompletedTask;
                       }

                       attempts++;
                       throw new InvalidOperationException("projection unavailable");
                   },
                   delay: (wait, _) => { waits.Add(wait); return Task.CompletedTask; },
                   jitter: () => 0.5)) // the midpoint, so the base schedule is what is asserted
        {
            await SettleUntilAsync(consumer, () => attempts == ContractConsumer.MaxAttempts);
        }

        Assert.Equal(3, attempts);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], waits); // between attempts, not after the last

        var dead = Assert.Single(ReadDeadLetters(key));
        Assert.Equal("3", Header(dead, "x-dlq-attempt-count"));
        Assert.Equal("HandlerFailed", Header(dead, "x-dlq-error-class"));
        Assert.Contains("projection unavailable", Header(dead, "x-dlq-reason"));
    }

    [Fact]
    public async Task IfTheDeadLetterProduceFails_TheOffsetIsNotCommitted_AndTheRecordIsRedelivered()
    {
        // A source topic with no .dlq beside it, and auto-creation off: the DLQ produce cannot
        // succeed. The record must not be committed past - it would be lost with nowhere recorded.
        var source = "kafka002.nodlq." + Guid.NewGuid().ToString("N")[..8] + ".v1";
        using (var admin = _broker.Admin())
        {
            await new TopicBootstrap(admin).RunAsync([new TopicSpec(source, 1, CleanupPolicy.Delete, TimeSpan.FromHours(1))]);
        }

        var key = Key();
        using var producer = Producer(messageTimeoutMs: 3_000);
        var at = await ProduceAsync(producer, source, key, "{ not json");

        var group = "nodlq-" + key;
        using (var consumer = new ContractConsumer(ConsumerConfig(group), source, Telemetry(), producer, (_, _) => Task.CompletedTask))
        {
            await Assert.ThrowsAsync<ProduceException<string, byte[]>>(async () =>
            {
                while (!await consumer.ProcessOneAsync(TimeSpan.FromSeconds(1)))
                {
                }
            });
        }

        using var probe = new ConsumerBuilder<string, byte[]>(ConsumerConfig(group)).Build();
        var committed = probe.Committed([at.TopicPartition], TimeSpan.FromSeconds(10)).Single();
        Assert.True(committed.Offset == Offset.Unset || committed.Offset.Value <= at.Offset.Value,
            $"committed {committed.Offset} although the record never reached the DLQ");

        // And a fresh consumer in the same group receives it again.
        probe.Subscribe(source);
        var again = probe.Consume(TimeSpan.FromSeconds(15));
        Assert.NotNull(again);
        Assert.Equal(at.Offset, again.Offset);
    }

    private static async Task SettleUntilAsync(ContractConsumer consumer, Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!done())
        {
            Assert.True(DateTime.UtcNow < deadline, "the consumer did not settle the expected records in time");
            await consumer.ProcessOneAsync(TimeSpan.FromSeconds(1));
        }
    }
}
