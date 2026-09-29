using System.Globalization;
using System.Text;
using Confluent.Kafka;

namespace Mair.EventBackbone;

/// <summary>
/// The one consume path every consumer is built on (<c>OD-009</c>, <c>KAFKA_EVENT_BACKBONE.md</c> §2.1).
/// <para>
/// Its scope is the rules in <c>KAFKA_TOPOLOGY_AND_SEMANTICS.md</c> §9 and nothing more: validate
/// against the authoritative schema · a schema-invalid or unparseable record goes to
/// <c>&lt;topic&gt;.dlq</c> on the <b>first</b> attempt · a handler failure is retried up to
/// <see cref="MaxAttempts"/> times with the §9 backoff, then DLQ'd · every required DLQ header is
/// set · the source offset is committed only <b>after</b> the DLQ produce succeeds · nothing is
/// redriven automatically.
/// </para>
/// <para>
/// It is deliberately not a framework: no routing, no handler registry, no configuration beyond the
/// topic, the schema and the handler. The Safety Supervisor's reject-then-DLQ exception (§9) is not
/// here either; that belongs to Phase 6.
/// </para>
/// </summary>
public sealed class ContractConsumer : IDisposable
{
    /// <summary>§9: "Max attempts before DLQ".</summary>
    public const int MaxAttempts = 3;

    /// <summary>§9: 1 s → 2 s → 4 s between attempts.</summary>
    private static readonly TimeSpan[] Backoff = [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4)];

    private readonly IConsumer<string, byte[]> _consumer;
    private readonly IProducer<string, byte[]> _deadLetters;
    private readonly ContractSchema _schema;
    private readonly Func<ConsumeResult<string, byte[]>, CancellationToken, Task> _handler;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<double> _jitter;
    private readonly TimeProvider _time;

    public ContractConsumer(
        ConsumerConfig config,
        string topic,
        ContractSchema schema,
        IProducer<string, byte[]> deadLetters,
        Func<ConsumeResult<string, byte[]>, CancellationToken, Task> handler,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<double>? jitter = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(deadLetters);
        ArgumentNullException.ThrowIfNull(handler);

        Topic = topic;
        _schema = schema;
        _deadLetters = deadLetters;
        _handler = handler;
        _delay = delay ?? Task.Delay;
        _jitter = jitter ?? Random.Shared.NextDouble;
        _time = time ?? TimeProvider.System;
        _consumer = new ConsumerBuilder<string, byte[]>(Harden(config)).Build();
        _consumer.Subscribe(topic);
    }

    public string Topic { get; }

    public string DeadLetterTopic => $"{Topic}.dlq";

    /// <summary>Records sent to the DLQ. Each one also raises an alert in §9's terms; that wiring is Phase 8.</summary>
    public long DeadLettered { get; private set; }

    public long Processed { get; private set; }

    /// <summary>
    /// The settings §6 and ADR-0021 B6 require, applied whatever the caller passed. A consumer that
    /// could be configured into auto-commit is a consumer that could silently become at-most-once.
    /// </summary>
    private static ConsumerConfig Harden(ConsumerConfig config) => new(config)
    {
        // §6: auto-commit converts at-least-once into silent at-most-once on a crash.
        EnableAutoCommit = false,
        EnableAutoOffsetStore = false,

        // ADR-0021 B6: pinned, not inherited. Kafka 4.x ships the KIP-848 protocol beside the
        // classic one, and which is in force changes rebalance and replay behaviour.
        GroupProtocol = GroupProtocol.Classic,
        PartitionAssignmentStrategy = PartitionAssignmentStrategy.Range,

        // §2: topics come from the register, never from a consumer subscribing to a name.
        AllowAutoCreateTopics = false,
    };

    /// <summary>
    /// Consumes and settles at most one record. Returns <c>false</c> if nothing arrived.
    /// <para>
    /// "Settles" means the record ends in exactly one place — handled, or on the DLQ — and only then
    /// is its offset committed. If the DLQ produce fails, the exception propagates and the offset is
    /// <b>not</b> committed, so the record is redelivered rather than lost.
    /// </para>
    /// </summary>
    public async Task<bool> ProcessOneAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var record = _consumer.Consume(timeout);
        if (record is null)
        {
            return false;
        }

        var verdict = _schema.Validate(record.Message.Value);
        if (!verdict.Valid)
        {
            // First attempt, no retry: a structurally invalid record cannot become valid (§9).
            await DeadLetterAsync(record, verdict.Rejection!.Value, verdict.Reason, attempts: 1, _time.GetUtcNow());
        }
        else
        {
            await HandleAsync(record, cancellationToken);
        }

        _consumer.Commit(record);
        Processed++;
        return true;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await ProcessOneAsync(TimeSpan.FromMilliseconds(500), cancellationToken);
        }
    }

    private async Task HandleAsync(ConsumeResult<string, byte[]> record, CancellationToken cancellationToken)
    {
        DateTimeOffset? firstFailure = null;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await _handler(record, cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                firstFailure ??= _time.GetUtcNow();

                if (attempt == MaxAttempts)
                {
                    await DeadLetterAsync(record, RejectionClass.HandlerFailed,
                        $"{ex.GetType().Name}: {ex.Message}", attempt, firstFailure.Value);
                    return;
                }

                // ±20 % jitter, as every retry in FAILURE_MODEL.md's rule is.
                var wait = Backoff[attempt - 1] * (0.8 + (0.4 * _jitter()));
                await _delay(wait, cancellationToken);
            }
        }
    }

    private async Task DeadLetterAsync(
        ConsumeResult<string, byte[]> record, RejectionClass rejection, string reason, int attempts, DateTimeOffset firstFailedAt)
    {
        var headers = new Headers();

        // The source record's own headers ride along; the DLQ adds to them, it does not replace them.
        foreach (var header in record.Message.Headers ?? [])
        {
            headers.Add(header.Key, header.GetValueBytes());
        }

        void Add(string key, string value) => headers.Add(key, Encoding.UTF8.GetBytes(value));

        // §9's required set, all seven.
        Add("x-dlq-reason", reason);
        Add("x-dlq-source-topic", record.Topic);
        Add("x-dlq-source-partition", record.Partition.Value.ToString(CultureInfo.InvariantCulture));
        Add("x-dlq-source-offset", record.Offset.Value.ToString(CultureInfo.InvariantCulture));
        Add("x-dlq-attempt-count", attempts.ToString(CultureInfo.InvariantCulture));
        Add("x-dlq-first-failed-at", firstFailedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        Add("x-dlq-error-class", rejection.ToString());

        // The original bytes, untouched. A payload that failed validation cannot be re-serialised
        // without inventing what it meant (ADR-0021).
        await _deadLetters.ProduceAsync(DeadLetterTopic, new Message<string, byte[]>
        {
            Key = record.Message.Key,
            Value = record.Message.Value,
            Headers = headers,
        });

        DeadLettered++;
    }

    public void Dispose()
    {
        _consumer.Close();
        _consumer.Dispose();
    }
}
