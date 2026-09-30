using System.Globalization;
using System.Text.Json;
using Confluent.Kafka;

namespace Mair.EdgeGateway;

/// <summary>Writes an <see cref="EquipmentStateRecord"/> as <c>equipment-state.schema.json</c> defines.</summary>
public static class EquipmentStateJson
{
    public static byte[] Serialize(EquipmentStateRecord r)
    {
        ArgumentNullException.ThrowIfNull(r);

        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("eventId", r.EventId);
            w.WriteString("eventType", "factory.equipment-state");
            w.WriteNumber("schemaVersion", 1);
            w.WriteString("equipmentId", r.EquipmentId);
            w.WriteString("state", Name(r.State));

            // A refresh reports no transition, so it carries neither field (OD-008). A transition
            // always carries both, even when the transition itself is null (OD-010).
            if (!r.IsRefresh)
            {
                w.WriteString("previousState", r.PreviousState is { } p ? Name(p) : null);
                w.WriteString("transitionId", r.TransitionId);
            }

            w.WriteNumber("stateSequence", r.StateSequence);
            w.WriteNumber("gatewayEpoch", r.GatewayEpoch);
            w.WriteString("observedBy", r.ObservedBy);
            w.WriteBoolean("aiEligible", r.AiEligible);

            // activeConditions: deliberately absent. Neither protocol carries them (OD-010).
            w.WriteString("occurredAtUtc", Timestamp(r.OccurredAtUtc));
            w.WriteString("ingestTimeUtc", Timestamp(r.IngestTimeUtc));
            w.WriteString("producer", r.ObservedBy);
            w.WriteString("correlationId", r.CorrelationId);
            w.WriteNull("causationId");
            w.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static string Name(EquipmentState s) => s.ToString().ToUpperInvariant();

    private static string Timestamp(DateTimeOffset t) =>
        t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

/// <summary>
/// Binds equipment-state records to <c>factory.equipment-states.v1</c>, keyed by equipment so
/// compaction keeps exactly one current record per machine (OD-008). Non-blocking, like
/// <see cref="KafkaTelemetrySink"/>, and for the same reason.
/// </summary>
public sealed class KafkaEquipmentStateSink : IEquipmentStateSink, IDisposable
{
    public const string Topic = "factory.equipment-states.v1";

    private readonly IProducer<string, byte[]?> _producer;
    private long _deliveryFailures;

    public KafkaEquipmentStateSink(string bootstrapServers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bootstrapServers);

        _producer = new ProducerBuilder<string, byte[]?>(new ProducerConfig
        {
            BootstrapServers = bootstrapServers,
            EnableIdempotence = true,
            Acks = Acks.All,

            // Topics come from the register only (§2). The test broker refuses auto-creation
            // globally; this makes the producer refuse it too, whatever broker it meets. Found
            // missing by Codex's Phase 3 verification.
            AllowAutoCreateTopics = false,
            SocketConnectionSetupTimeoutMs = 10_000,
            MessageTimeoutMs = 30_000,
            RetryBackoffMs = 100,
            RetryBackoffMaxMs = 3_200,
        }).Build();
    }

    public long DeliveryFailures => Interlocked.Read(ref _deliveryFailures);

    public void Emit(EquipmentStateRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Produce(record.EquipmentId, EquipmentStateJson.Serialize(record));
    }

    public void Tombstone(string equipmentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(equipmentId);

        // A null value is what compaction treats as "delete this key". Nothing else ever removes a
        // machine from a compact-only topic.
        Produce(equipmentId, null);
    }

    private void Produce(string key, byte[]? value) =>
        _producer.Produce(Topic, new Message<string, byte[]?> { Key = key, Value = value }, report =>
        {
            if (report.Error.IsError)
            {
                Interlocked.Increment(ref _deliveryFailures);
            }
        });

    public int Flush(TimeSpan timeout) => _producer.Flush(timeout);

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(30));
        _producer.Dispose();
    }
}
