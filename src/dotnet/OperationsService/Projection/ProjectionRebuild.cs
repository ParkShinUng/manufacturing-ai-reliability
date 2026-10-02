using Confluent.Kafka;
using Mair.EventBackbone;
using Npgsql;

namespace Mair.OperationsService.Projection;

/// <summary>
/// A rebuild is a <b>range operation</b> (<c>OD-014</c>): rewind the projector group to an offset per
/// partition, delete exactly the rows whose provenance lies at or after it, and let the projector
/// replay. Nothing is truncated: rows from earlier offsets, and history Kafka no longer holds, are
/// never touched.
/// <para>
/// The projector must be stopped: Kafka refuses to move a group's offsets while it has members, and
/// <see cref="ReplayTool"/> refuses a group the register does not make replay-eligible.
/// </para>
/// </summary>
public static class ProjectionRebuild
{
    /// <summary>
    /// The tables a rebuild may delete from, per topic (OD-023): only those whose topic keeps every
    /// record within retention, so a replay restores what was deleted. The audit tables are
    /// insert-only, and the tables of compacted topics would not get their history back: for those
    /// topics the rewind alone runs, and the replay re-inserts only what is missing.
    /// </summary>
    private static readonly Dictionary<string, string[]> TablesByTopic = new()
    {
        ["factory.telemetry.v1"] = ["telemetry_reading_1s"],
        ["factory.predictions.v1"] = ["prediction"],
        ["factory.equipment-states.v1"] = [],
        ["factory.safety-decisions.v1"] = [],
        ["factory.control-outcomes.v1"] = [],
        ["factory.model-deployments.v1"] = [],
    };

    /// <returns>The rows deleted, to be written back by the replay.</returns>
    public static async Task<long> ReplaceFromAsync(
        IAdminClient admin, NpgsqlDataSource db, string group, IReadOnlyList<TopicPartitionOffset> from, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(from);
        foreach (var start in from)
        {
            if (!TablesByTopic.ContainsKey(start.Topic))
            {
                throw new ArgumentException($"{start.Topic} is not a projected topic", nameof(from));
            }
        }

        // Rewind first: if it is refused - an ineligible group, or one with live members - nothing
        // has been deleted. If the delete then failed, the replay would rewrite identical rows.
        await ReplayTool.RewindAsync(admin, group, from);

        await using var conn = await db.OpenConnectionAsync(cancellationToken);
        await using var tx = await conn.BeginTransactionAsync(cancellationToken);
        long deleted = 0;
        foreach (var start in from)
        {
            foreach (var table in TablesByTopic[start.Topic])
            {
                await using var cmd = new NpgsqlCommand(
                    $"DELETE FROM operations.{table} WHERE source_topic = $1 AND source_partition = $2 AND source_offset >= $3", conn, tx);
                cmd.Parameters.Add(new NpgsqlParameter { Value = start.Topic });
                cmd.Parameters.Add(new NpgsqlParameter { Value = start.Partition.Value });
                cmd.Parameters.Add(new NpgsqlParameter { Value = start.Offset.Value });
                deleted += await cmd.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await tx.CommitAsync(cancellationToken);
        return deleted;
    }
}
