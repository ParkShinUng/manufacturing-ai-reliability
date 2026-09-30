using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace Mair.EventBackbone.Tests;

/// <summary>
/// <b>KAFKA-001 / AC-026</b> — every topic exists with the documented partition count, replication
/// factor and retention; the bootstrap is idempotent; and it <b>asserts</b> partition-count
/// immutability rather than enforcing it by hoping nobody calls the mutating API.
/// <para>
/// Against a real broker, because none of this is a property of the code alone: what the cluster
/// actually holds is the thing under test.
/// </para>
/// </summary>
public sealed class TopicBootstrapTests : IClassFixture<KafkaBroker>
{
    private readonly KafkaBroker _broker;

    public TopicBootstrapTests(KafkaBroker broker) => _broker = broker;

    private static async Task<Dictionary<string, string>> ConfigOf(IAdminClient admin, string topic)
    {
        var described = await admin.DescribeConfigsAsync(
            [new ConfigResource { Type = ResourceType.Topic, Name = topic }]);
        return described.Single().Entries.ToDictionary(e => e.Key, e => e.Value.Value);
    }

    [Fact]
    public async Task TheFirstRunCreatesTheRegister_TheSecondChangesNothing_AndAThirdWithADifferentPartitionCountRefuses()
    {
        using var admin = _broker.Admin();
        var bootstrap = new TopicBootstrap(admin);

        // 1. From nothing.
        var first = await bootstrap.RunAsync(TopicRegister.All);

        Assert.Equal(TopicRegister.All.Count, first.Created.Count);
        Assert.Empty(first.Unchanged);
        Assert.Equal(14, TopicRegister.All.Count); // seven source topics, each with its .dlq (§9)

        // Every topic, as the register documents it.
        var metadata = admin.GetMetadata(TimeSpan.FromSeconds(30));
        foreach (var spec in TopicRegister.All)
        {
            var topic = metadata.Topics.Single(t => t.Topic == spec.Name);
            Assert.Equal(spec.Partitions, topic.Partitions.Count);
            Assert.All(topic.Partitions, p => Assert.Single(p.Replicas)); // RF 1 locally (§2)

            var config = await ConfigOf(admin, spec.Name);
            Assert.Equal(spec.Cleanup == CleanupPolicy.Compact ? "compact" : "delete", config["cleanup.policy"]);
            Assert.Equal(
                spec.Retention is { } r ? ((long)r.TotalMilliseconds).ToString() : "-1",
                config["retention.ms"]);
            Assert.Equal("1", config["min.insync.replicas"]); // §6, local
        }

        // OD-008, asserted by name because it is the one the contract changed: infinite, compact,
        // no deletion — a machine stable for a week must not lose its only state record.
        var states = await ConfigOf(admin, "factory.equipment-states.v1");
        Assert.Equal("compact", states["cleanup.policy"]);
        Assert.Equal("-1", states["retention.ms"]);

        // 2. Again, unchanged.
        var second = await bootstrap.RunAsync(TopicRegister.All);

        Assert.Empty(second.Created);
        Assert.Equal(TopicRegister.All.Count, second.Unchanged.Count);

        // 3. The same register with one partition count altered.
        var altered = TopicRegister.All
            .Select(s => s.Name == "factory.telemetry.v1" ? s with { Partitions = 24 } : s)
            .ToList();

        var refused = await Assert.ThrowsAsync<TopicBootstrapException>(() => bootstrap.RunAsync(altered));

        Assert.Contains("factory.telemetry.v1", refused.Message);
        Assert.Contains("12", refused.Message);

        // And it refused rather than mutated: the live topic is untouched.
        var after = admin.GetMetadata(TimeSpan.FromSeconds(30));
        Assert.Equal(12, after.Topics.Single(t => t.Topic == "factory.telemetry.v1").Partitions.Count);
    }

    [Fact]
    public async Task ATopicThatDoesNotMatchTheRegisterIsRefused_NotQuietlyCorrected()
    {
        // Drift is an existing topic whose settings differ from the register - however it got that
        // way. Here it is created with a six-minute retention and the register asks for six hours.
        using var admin = _broker.Admin();
        var spec = new TopicSpec("bootstrap.drift.test.v1", 3, CleanupPolicy.Delete, TimeSpan.FromHours(6));
        var bootstrap = new TopicBootstrap(admin);

        await new TopicBootstrap(admin).RunAsync(
            [spec with { Retention = TimeSpan.FromMinutes(6) }]);

        var refused = await Assert.ThrowsAsync<TopicBootstrapException>(() => bootstrap.RunAsync([spec]));

        Assert.Contains("retention.ms", refused.Message);

        // Refused, and left alone: the bootstrap does not "fix" what it found.
        Assert.Equal("360000", (await ConfigOf(admin, spec.Name))["retention.ms"]);
    }

    [Fact]
    public async Task ATopicWithTheWrongReplicationFactorIsRefused()
    {
        // One broker can only hold RF 1, so the drift is produced from the other side: a topic
        // created locally, then checked as the production-like register would check it, which says
        // RF 3. Its own topic, so it does not depend on the order the other tests run in.
        using var admin = _broker.Admin();
        var spec = new TopicSpec("bootstrap.rf.test.v1", 3, CleanupPolicy.Delete, TimeSpan.FromHours(6));
        await new TopicBootstrap(admin).RunAsync([spec]);

        var refused = await Assert.ThrowsAsync<TopicBootstrapException>(
            () => new TopicBootstrap(admin, productionLike: true).RunAsync([spec]));

        Assert.Contains("replication factor", refused.Message);
    }

    [Fact]
    public async Task NoTopicIsAutoCreatedByAProducerOrConsumer()
    {
        // §2's register is the only thing that creates topics. Auto-creation would give a producer
        // a topic with the broker's default partition count, which is how per-key ordering is lost
        // without anyone deciding to lose it.
        using var admin = _broker.Admin();
        const string never = "factory.never-registered.v1";

        using var producer = new ProducerBuilder<string, string>(new ProducerConfig
        {
            BootstrapServers = _broker.BootstrapServers,
            MessageTimeoutMs = 5_000,
            AllowAutoCreateTopics = false,
        }).Build();

        await Assert.ThrowsAsync<ProduceException<string, string>>(
            () => producer.ProduceAsync(never, new Message<string, string> { Key = "eq-001", Value = "{}" }));

        var metadata = admin.GetMetadata(TimeSpan.FromSeconds(30));
        Assert.DoesNotContain(metadata.Topics, t => t.Topic == never && t.Error.Code == ErrorCode.NoError);
    }

    [Fact]
    public async Task TwoBootstrapsRacingOverTheSameNewTopicsBothSucceed_AndOnlyOneCreatesEach()
    {
        // Two gateways starting together: each may see the topics missing, one creation wins and the
        // other gets TopicAlreadyExists. The loser must verify the topics, not fail or claim them
        // (CLD-P3-001, P3-COD-004). Repeated because one round may not race.
        using var admin = _broker.Admin();
        for (var round = 0; round < 5; round++)
        {
            var specs = Enumerable.Range(0, 3)
                .Select(i => new TopicSpec($"bootstrap.race.{round}.{i}.{Guid.NewGuid():N}.v1", 2, CleanupPolicy.Delete, TimeSpan.FromHours(1)))
                .ToList();

            var runs = await Task.WhenAll(
                Task.Run(() => new TopicBootstrap(admin).RunAsync(specs)),
                Task.Run(() => new TopicBootstrap(admin).RunAsync(specs)));

            foreach (var spec in specs)
            {
                Assert.Equal(1, runs.Count(r => r.Created.Contains(spec.Name)));
                Assert.Equal(1, runs.Count(r => r.Unchanged.Contains(spec.Name)));
            }
        }
    }
}
