using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace Mair.EventBackbone;

/// <summary>A topic on the cluster does not match the register, and the bootstrap will not change it.</summary>
public sealed class TopicBootstrapException(string message) : Exception(message);

/// <summary>What one bootstrap run did.</summary>
/// <param name="Created">Topics that did not exist and were created.</param>
/// <param name="Unchanged">Topics that already matched the register.</param>
public sealed record BootstrapResult(IReadOnlyList<string> Created, IReadOnlyList<string> Unchanged);

/// <summary>
/// Creates the topic register on a cluster, and refuses to alter a topic that is already there
/// (<c>AC-026</c>, <c>KAFKA-001</c>).
/// <para>
/// <b>Idempotent by describing, not by trying.</b> A second run over an unchanged cluster makes no
/// call that could change anything: every existing topic is described and compared, and only
/// genuinely missing topics are created.
/// </para>
/// <para>
/// <b>Partition-count immutability is this repository's rule, not Kafka's</b> (ADR-0021 B4).
/// <c>AdminClient</c> offers <c>CreatePartitionsAsync</c> and the broker will honour it, so nothing
/// in the client stops a partition count from moving. This class therefore never calls it. The
/// reason is in §4: partitioning is by key, and adding a partition re-keys the topic, so records
/// for one equipment land on two partitions and per-key ordering — which the whole ordering
/// argument rests on — is silently gone for every key already in the log.
/// </para>
/// </summary>
public sealed class TopicBootstrap(IAdminClient admin, bool productionLike = false)
{
    private static readonly TimeSpan AdminTimeout = TimeSpan.FromSeconds(30);

    public async Task<BootstrapResult> RunAsync(
        IReadOnlyList<TopicSpec> register, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(register);

        var metadata = admin.GetMetadata(AdminTimeout);
        var existing = metadata.Topics
            .Where(t => t.Error.Code == ErrorCode.NoError)
            .ToDictionary(t => t.Topic, t => t.Partitions.Count);

        var created = new List<string>();
        var unchanged = new List<string>();

        foreach (var spec in register)
        {
            if (existing.TryGetValue(spec.Name, out var partitions))
            {
                await VerifyAsync(spec, partitions, cancellationToken);
                unchanged.Add(spec.Name);
            }
            else
            {
                created.Add(spec.Name);
            }
        }

        if (created.Count > 0)
        {
            await admin.CreateTopicsAsync(
                register.Where(s => created.Contains(s.Name)).Select(Specify),
                new CreateTopicsOptions { RequestTimeout = AdminTimeout, OperationTimeout = AdminTimeout });
        }

        return new BootstrapResult(created, unchanged);
    }

    /// <summary>Describes what is there and fails on any difference. Nothing here mutates.</summary>
    private async Task VerifyAsync(TopicSpec spec, int partitions, CancellationToken cancellationToken)
    {
        if (partitions != spec.Partitions)
        {
            throw new TopicBootstrapException(
                $"{spec.Name} has {partitions} partitions, the register says {spec.Partitions}. " +
                "Refusing to change it: re-partitioning a keyed topic re-keys every future record " +
                "and breaks the per-key ordering already relied on (KAFKA_TOPOLOGY_AND_SEMANTICS.md §4).");
        }

        var resource = new ConfigResource { Type = ResourceType.Topic, Name = spec.Name };
        var described = await admin.DescribeConfigsAsync([resource], new DescribeConfigsOptions { RequestTimeout = AdminTimeout });
        var live = described.Single().Entries;

        foreach (var (key, expected) in Configuration(spec, productionLike))
        {
            var actual = live.TryGetValue(key, out var entry) ? entry.Value : "(absent)";
            if (actual != expected)
            {
                throw new TopicBootstrapException(
                    $"{spec.Name} has {key}={actual}, the register says {expected}. Refusing to change it: " +
                    "a topic that drifted from the contract is a question for a human, not something " +
                    "a bootstrap job should quietly correct.");
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private TopicSpecification Specify(TopicSpec spec) => new()
    {
        Name = spec.Name,
        NumPartitions = spec.Partitions,
        ReplicationFactor = TopicSpec.ReplicationFactor(productionLike),
        Configs = Configuration(spec, productionLike),
    };

    private static Dictionary<string, string> Configuration(TopicSpec spec, bool productionLike) => new()
    {
        // §6: the required companion to acks=all. Without it, acks=all silently degrades to a single
        // replica whenever the in-sync set shrinks. 1 locally, where there is one broker; 2
        // production-like, with three replicas.
        ["min.insync.replicas"] = productionLike ? "2" : "1",
        ["cleanup.policy"] = spec.Cleanup == CleanupPolicy.Compact ? "compact" : "delete",

        // -1 is Kafka's infinite retention. A compacted state topic needs it: §5 has the Supervisor
        // learn current state by reading the topic in full (OD-008).
        ["retention.ms"] = spec.Retention is { } r ? ((long)r.TotalMilliseconds).ToString() : "-1",
    };
}
