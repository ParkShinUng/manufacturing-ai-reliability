using Confluent.Kafka;

namespace Mair.EdgeGateway;

/// <summary>
/// Binds the egress port to <c>factory.telemetry.v1</c> (<c>EDGE_GATEWAY.md</c> §5.1, FR-010).
/// <para>
/// <b>Never blocks the OT poll loop</b> (§4). <c>Produce</c> hands the record to the client's local
/// queue and returns; delivery happens on the client's own thread. If that queue is full the call
/// throws, the poll loop counts an egress failure, and the record stays in the bounded buffer for
/// the next cycle (<c>EquipmentPollLoop</c>'s peek-emit-dequeue). Kafka trouble therefore degrades
/// into counted, bounded loss — never into missed samples.
/// </para>
/// <para>
/// A record the client accepted and then failed to deliver within the 30 s delivery timeout
/// (<c>KAFKA_TOPOLOGY_AND_SEMANTICS.md</c> §11) is counted in <see cref="DeliveryFailures"/>. By then
/// it has left the buffer; the count is how that loss stays visible rather than silent.
/// </para>
/// </summary>
public sealed class KafkaTelemetrySink : IEgressSink, IDisposable
{
    public const string Topic = "factory.telemetry.v1";

    private readonly IProducer<string, byte[]> _producer;
    private long _delivered;
    private long _deliveryFailures;
    private string? _lastDeliveryFailure;

    public KafkaTelemetrySink(string bootstrapServers)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bootstrapServers);

        _producer = new ProducerBuilder<string, byte[]>(new ProducerConfig
        {
            BootstrapServers = bootstrapServers,

            // §6: idempotent, acks=all. Idempotence also bounds retries by time rather than count,
            // which is why §11 states the producer's budget as a 30 s delivery timeout.
            EnableIdempotence = true,
            Acks = Acks.All,

            // §11, numerically.
            SocketConnectionSetupTimeoutMs = 10_000,
            MessageTimeoutMs = 30_000,
            RetryBackoffMs = 100,
            RetryBackoffMaxMs = 3_200,
        }).Build();
    }

    public long Delivered => Interlocked.Read(ref _delivered);

    /// <summary><c>telemetry_publish_errors_total</c> for records that left the buffer and then failed.</summary>
    public long DeliveryFailures => Interlocked.Read(ref _deliveryFailures);

    public string? LastDeliveryFailure => Volatile.Read(ref _lastDeliveryFailure);

    public void Emit(CanonicalTelemetry telemetry)
    {
        ArgumentNullException.ThrowIfNull(telemetry);

        // Keyed by equipment: per-key ordering on one partition is what §4's ordering argument
        // rests on, and the equipment is the unit whose sequence must stay in order.
        _producer.Produce(Topic, new Message<string, byte[]>
        {
            Key = telemetry.EquipmentId,
            Value = TelemetryJson.Serialize(telemetry),
        }, OnDelivery);
    }

    private void OnDelivery(DeliveryReport<string, byte[]> report)
    {
        if (report.Error.IsError)
        {
            Interlocked.Increment(ref _deliveryFailures);
            Volatile.Write(ref _lastDeliveryFailure, $"{report.Error.Code}: {report.Error.Reason}");
        }
        else
        {
            Interlocked.Increment(ref _delivered);
        }
    }

    /// <summary>Waits for what is already queued, up to the delivery timeout. Used at shutdown.</summary>
    public int Flush(TimeSpan timeout) => _producer.Flush(timeout);

    public void Dispose()
    {
        _producer.Flush(TimeSpan.FromSeconds(30));
        _producer.Dispose();
    }
}
