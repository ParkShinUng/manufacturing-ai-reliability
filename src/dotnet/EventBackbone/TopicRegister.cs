namespace Mair.EventBackbone;

/// <summary>How a topic's old records are removed. `compact` keeps the last record per key.</summary>
public enum CleanupPolicy
{
    Delete,
    Compact,
}

/// <summary>
/// One row of the topic register in <c>KAFKA_TOPOLOGY_AND_SEMANTICS.md</c> §2.
/// </summary>
/// <param name="Name">Topic name. §1's plural convention: <c>factory.&lt;aggregate-plural&gt;.v&lt;major&gt;</c>.</param>
/// <param name="Partitions">
/// Pinned. Changing it re-keys a keyed topic and destroys per-key ordering (§4), which is why the
/// bootstrap refuses rather than adjusts.
/// </param>
/// <param name="Retention">
/// <c>null</c> means infinite, which a compacted state topic needs: the Supervisor learns current
/// state by reading the topic in full, so the last record per key must outlive any retention window
/// (OD-008).
/// </param>
/// <param name="AllowsTombstones">
/// The register's <i>Tombstones</i> column (OD-021). Only where it is set is a null value a record —
/// passed to the consumer's tombstone handler — rather than an invalid one for the DLQ.
/// </param>
public sealed record TopicSpec(
    string Name,
    int Partitions,
    CleanupPolicy Cleanup,
    TimeSpan? Retention,
    bool AllowsTombstones = false)
{
    /// <summary>
    /// Replication factor. **1 locally, 3 production-like** (§2). One broker cannot host three
    /// replicas, so this is a property of the deployment rather than of the topic.
    /// </summary>
    public static short ReplicationFactor(bool productionLike) => (short)(productionLike ? 3 : 1);

    /// <summary>The dead-letter topic for this one: same name plus <c>.dlq</c>, same shape, 30 d (§9).</summary>
    public TopicSpec DeadLetter() => new($"{Name}.dlq", Partitions, CleanupPolicy.Delete, TimeSpan.FromDays(30));
}

/// <summary>
/// The topic register, transcribed from <c>KAFKA_TOPOLOGY_AND_SEMANTICS.md</c> §2 and §9.
/// <para>
/// It is data, not configuration: the contract fixes these numbers, and a deployment that wants
/// different ones is asking for a contract change. `AC-026` asserts the live cluster matches this.
/// </para>
/// </summary>
public static class TopicRegister
{
    private static readonly TopicSpec[] Sources =
    [
        new("factory.telemetry.v1", 12, CleanupPolicy.Delete, TimeSpan.FromHours(6)),
        new("factory.predictions.v1", 12, CleanupPolicy.Delete, TimeSpan.FromHours(24)),
        new("factory.safety-decisions.v1", 12, CleanupPolicy.Delete, TimeSpan.FromDays(7)),
        new("factory.control-outcomes.v1", 12, CleanupPolicy.Delete, TimeSpan.FromDays(7)),

        // Infinite, and `compact` with no `delete` (OD-008). It was `compact+delete` at 7 d, which
        // contradicted §5's requirement that the Supervisor learn current state by reading the
        // topic in full: a machine stable for longer than the retention lost its only record.
        // The one topic with tombstones: the gateway's way for equipment to leave (OD-008, OD-021).
        new("factory.equipment-states.v1", 12, CleanupPolicy.Compact, null, AllowsTombstones: true),

        new("factory.model-deployments.v1", 1, CleanupPolicy.Compact, null),
        new("factory.faults.v1", 3, CleanupPolicy.Delete, TimeSpan.FromDays(7)),
    ];

    /// <summary>The seven source topics of §2, without their dead-letter topics.</summary>
    public static IReadOnlyList<TopicSpec> SourceTopics => Sources;

    /// <summary>Every topic the bootstrap creates: the seven, each with its <c>.dlq</c> (§9).</summary>
    public static IReadOnlyList<TopicSpec> All { get; } =
        [.. Sources, .. Sources.Select(t => t.DeadLetter())];

    /// <summary>The register's entry for a topic, or <c>null</c> if it has none.</summary>
    public static TopicSpec? Find(string name) => All.FirstOrDefault(t => t.Name == name);
}
