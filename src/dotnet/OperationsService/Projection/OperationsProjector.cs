using Confluent.Kafka;
using Mair.EventBackbone;
using Npgsql;

namespace Mair.OperationsService.Projection;

/// <summary>
/// The operations projector: one shared <see cref="ContractConsumer"/> per projected topic, all in
/// <c>cg.operations-projector.v1</c> (replay-eligible, <c>KAFKA_TOPOLOGY_AND_SEMANTICS.md</c> §3),
/// each writing through <see cref="Projections"/>.
/// <para>
/// <c>factory.faults.v1</c> is not projected: it has no contract until Phase 11 (OD-020).
/// </para>
/// </summary>
public sealed class OperationsProjector : IDisposable
{
    private readonly List<ContractConsumer> _consumers = [];

    /// <param name="contracts">The directory holding the contract schemas, <c>contracts/jsonschema/v1</c>.</param>
    /// <param name="groupId">The registered group, unless a test needs one of its own.</param>
    public OperationsProjector(
        string bootstrapServers,
        NpgsqlDataSource db,
        string contracts,
        IProducer<string, byte[]> deadLetters,
        string groupId = ConsumerGroupRegister.OperationsProjector)
    {
        var write = new Projections(db);
        var config = new ConsumerConfig
        {
            BootstrapServers = bootstrapServers,
            GroupId = groupId,
            // The register's reset for this group: a projection is built from the start (§3).
            AutoOffsetReset = AutoOffsetReset.Earliest,
        };

        ContractConsumer Consume(string topic, string schema, Func<ConsumeResult<string, byte[]>, CancellationToken, Task> handler,
            Func<ConsumeResult<string, byte[]>, CancellationToken, Task>? tombstones = null) =>
            new(config, topic, ContractSchema.Load(Path.Combine(contracts, schema)), deadLetters, handler, tombstoneHandler: tombstones);

        _consumers.Add(Consume("factory.telemetry.v1", "telemetry.schema.json", write.TelemetryAsync));
        _consumers.Add(Consume("factory.equipment-states.v1", "equipment-state.schema.json", write.EquipmentStateAsync, write.DecommissionAsync));
        _consumers.Add(Consume("factory.predictions.v1", "prediction.schema.json", write.PredictionAsync));
        _consumers.Add(Consume("factory.safety-decisions.v1", "safety-decision.schema.json", write.SafetyDecisionAsync));
        _consumers.Add(Consume("factory.control-outcomes.v1", "control-outcome.schema.json", write.ControlOutcomeAsync));
        _consumers.Add(Consume("factory.model-deployments.v1", "model-authorization.schema.json", write.ModelDeploymentAsync));
    }

    public IReadOnlyList<ContractConsumer> Consumers => _consumers;

    /// <summary>
    /// Every consumer on its own loop. They share one group, and in the classic protocol a rebalance
    /// completes only when every member polls: polling them in turn from one thread made a group of
    /// six take minutes to settle its assignment.
    /// </summary>
    public Task RunAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(_consumers.Select(c => Task.Run(() => c.RunAsync(cancellationToken), CancellationToken.None)));

    /// <summary>Every consumer has been caught up at or after <paramref name="since"/> (OD-018).</summary>
    public bool CaughtUpSince(DateTimeOffset since) => _consumers.All(c => c.LastCaughtUpUtc >= since);

    public void Dispose()
    {
        foreach (var consumer in _consumers)
        {
            consumer.Dispose();
        }
    }
}
