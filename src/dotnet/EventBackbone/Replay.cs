using Confluent.Kafka;
using Confluent.Kafka.Admin;

namespace Mair.EventBackbone;

/// <summary>Whether a group may be rewound. <c>KAFKA_TOPOLOGY_AND_SEMANTICS.md</c> §3's column.</summary>
public enum ReplayEligibility
{
    /// <summary>Rebuildable read models: replay is the point.</summary>
    Eligible,

    /// <summary>Analytics groups: replayable, offline.</summary>
    OfflineOnly,

    /// <summary>DLQ redrive: operator-initiated, and this tool is how an operator initiates it.</summary>
    ManualOnly,

    /// <summary>The Safety Supervisor. Replay would re-issue real equipment commands (DEC-002).</summary>
    Never,
}

public sealed record ConsumerGroupSpec(string Name, ReplayEligibility Replay, AutoOffsetReset OffsetReset);

/// <summary>§3, transcribed. The only groups this repository runs.</summary>
public static class ConsumerGroupRegister
{
    public const string SafetySupervisor = "cg.safety-supervisor.v1";
    public const string OperationsProjector = "cg.operations-projector.v1";

    public static IReadOnlyList<ConsumerGroupSpec> All { get; } =
    [
        new(SafetySupervisor, ReplayEligibility.Never, AutoOffsetReset.Latest),
        new(OperationsProjector, ReplayEligibility.Eligible, AutoOffsetReset.Earliest),
        new("cg.feature-builder.v1", ReplayEligibility.OfflineOnly, AutoOffsetReset.Latest),
        new("cg.shadow-feature-builder.v1", ReplayEligibility.OfflineOnly, AutoOffsetReset.Latest),
    ];

    /// <summary>
    /// A registered group, or a DLQ redrive group — <c>cg.&lt;name&gt;.dlq-redrive</c>, which §3 lists as
    /// a pattern rather than one name.
    /// </summary>
    public static ConsumerGroupSpec? Find(string group) =>
        All.FirstOrDefault(g => g.Name == group)
        ?? (group.StartsWith("cg.", StringComparison.Ordinal) && group.EndsWith(".dlq-redrive", StringComparison.Ordinal)
            ? new ConsumerGroupSpec(group, ReplayEligibility.ManualOnly, AutoOffsetReset.Earliest)
            : null);
}

/// <summary>A rewind this repository will not perform, and why.</summary>
public sealed class ReplayRefusedException(string message) : Exception(message);

/// <summary>
/// Rewinds a consumer group over a known offset range (<c>AC-045</c>, <c>FAIL-KAFKA-001</c>).
/// <para>
/// <b>Refuses the Safety Supervisor's group, and any group it does not know.</b> DEC-002 gives the
/// Supervisor's group no documented reset procedure because replaying predictions into it would
/// re-issue real equipment commands; this tool is the documented reset procedure for everything
/// else, so it is where that refusal has to live. An unknown group is refused too: failing closed on
/// a name nobody registered is cheaper than discovering later what it was.
/// </para>
/// <para>
/// The refusal is one of three defences, not the only one (§5). A rewind done by hand with the Kafka
/// CLI would bypass it — which is why the Supervisor also seeks to the end on assignment (§6.1) and
/// the prediction TTL is measured against wall-clock time.
/// </para>
/// </summary>
public static class ReplayTool
{
    /// <summary>
    /// Sets <paramref name="group"/>'s committed offsets to <paramref name="from"/>, so its next
    /// consumer starts there. Kafka requires the group to have no active members while this runs.
    /// </summary>
    public static async Task RewindAsync(IAdminClient admin, string group, IReadOnlyList<TopicPartitionOffset> from)
    {
        ArgumentNullException.ThrowIfNull(admin);
        ArgumentException.ThrowIfNullOrWhiteSpace(group);
        ArgumentNullException.ThrowIfNull(from);

        var spec = ConsumerGroupRegister.Find(group)
            ?? throw new ReplayRefusedException(
                $"'{group}' is not in the consumer-group register (KAFKA_TOPOLOGY_AND_SEMANTICS.md §3). Refusing to rewind a group nobody registered.");

        if (spec.Replay == ReplayEligibility.Never)
        {
            throw new ReplayRefusedException(
                $"'{group}' is not replay-eligible. Replaying predictions into the Safety Supervisor would re-issue " +
                "real equipment commands; it has no reset procedure by design (DEC-002, ADR-0012).");
        }

        await admin.AlterConsumerGroupOffsetsAsync([new ConsumerGroupTopicPartitionOffsets(group, from.ToList())]);
    }
}
