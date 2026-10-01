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
/// redriven automatically · a tombstone is a record only on a topic whose register entry allows one
/// (<c>OD-021</c>), and goes to the tombstone handler under the same retry and DLQ rules.
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
    private readonly Func<ConsumeResult<string, byte[]>, CancellationToken, Task>? _tombstoneHandler;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly Func<double> _jitter;
    private readonly TimeProvider _time;
    private readonly bool _resetToLatest;

    public ContractConsumer(
        ConsumerConfig config,
        string topic,
        ContractSchema schema,
        IProducer<string, byte[]> deadLetters,
        Func<ConsumeResult<string, byte[]>, CancellationToken, Task> handler,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        Func<double>? jitter = null,
        TimeProvider? time = null,
        bool seekToEndOnAssignment = false,
        Func<ConsumeResult<string, byte[]>, CancellationToken, Task>? tombstoneHandler = null,
        TopicSpec? spec = null)
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

        // OD-021: whether a null value is a record is the topic's property, read from the register
        // (or the spec given for a topic outside it), never inferred from the name.
        var allowsTombstones = (spec ?? TopicRegister.Find(topic))?.AllowsTombstones ?? false;
        if (tombstoneHandler is not null && !allowsTombstones)
        {
            throw new ArgumentException($"{topic} does not allow tombstones, so it cannot have a tombstone handler", nameof(tombstoneHandler));
        }

        if (tombstoneHandler is null && allowsTombstones)
        {
            // Dead-lettering a legitimate decommissioning is the defect OD-021 exists to prevent.
            throw new ArgumentException($"{topic} allows tombstones; a consumer of it must say what one means", nameof(tombstoneHandler));
        }

        _tombstoneHandler = tombstoneHandler;
        _delay = delay ?? Task.Delay;
        _jitter = jitter ?? Random.Shared.NextDouble;
        _time = time ?? TimeProvider.System;
        _resetToLatest = config.AutoOffsetReset == AutoOffsetReset.Latest;
        var builder = new ConsumerBuilder<string, byte[]>(Harden(config));

        if (seekToEndOnAssignment)
        {
            // §6.1: the Safety Supervisor discards any backlog on factory.predictions.v1 at every
            // start. It has to be done here, on assignment: `auto.offset.reset=latest` applies only
            // to a group with no committed offset, so a restarting consumer with committed offsets
            // would otherwise grind through the backlog. And a seek issued before assignment silently
            // does nothing, because there is not yet a partition to seek.
            builder.SetPartitionsAssignedHandler((_, partitions) =>
                partitions.Select(p => new TopicPartitionOffset(p, Offset.End)));
        }

        _consumer = builder.Build();
        _consumer.Subscribe(topic);
    }

    public string Topic { get; }

    public string DeadLetterTopic => $"{Topic}.dlq";

    /// <summary>Records sent to the DLQ. Each one also raises an alert in §9's terms; that wiring is Phase 8.</summary>
    public long DeadLettered { get; private set; }

    private readonly Dictionary<RejectionClass, long> _deadLetteredBy = [];
    private readonly Dictionary<TopicPartition, long> _lag = [];
    private readonly Dictionary<TopicPartition, DateTimeOffset> _newest = [];

    /// <summary><c>dlq_messages_total{topic,reason}</c> for this consumer's topic.</summary>
    public IReadOnlyDictionary<RejectionClass, long> DeadLetteredByReason => _deadLetteredBy;

    /// <summary>
    /// <c>kafka_consumer_lag{group,topic,partition}</c>: records between the newest one settled and
    /// the partition's high watermark, as of the last fetch. Secondary signal (§12).
    /// </summary>
    public IReadOnlyDictionary<TopicPartition, long> Lag => _lag;

    /// <summary>
    /// <c>prediction_record_age_seconds{partition}</c> and its equivalent for every topic: the age of
    /// the <b>newest settled record</b>, per assigned partition, measured from the record's own
    /// timestamp. The primary signal (§12) — an aggregate offset lag can look healthy while one
    /// partition starves, and record age cannot.
    /// </summary>
    public IReadOnlyDictionary<TopicPartition, TimeSpan> RecordAge()
    {
        var now = _time.GetUtcNow();
        return _newest.ToDictionary(kv => kv.Key, kv => now - kv.Value);
    }

    public long Processed { get; private set; }

    /// <summary>Partitions currently assigned to this consumer by its group.</summary>
    public int AssignedPartitions => _consumer.Assignment.Count;

    /// <summary>
    /// <b>Caught up</b>, as <c>OD-018</c> defines it: the consumer holds an assignment, and on every
    /// assigned partition it has settled — handled or dead-lettered, and committed — every record
    /// below the high watermark the broker reports now. Queries the broker; not for the hot path.
    /// </summary>
    public bool IsCaughtUp(TimeSpan timeout)
    {
        var assignment = _consumer.Assignment;
        if (assignment.Count == 0)
        {
            return false;
        }

        var committed = _consumer.Committed(assignment, timeout).ToDictionary(c => c.TopicPartition, c => c.Offset);
        foreach (var partition in assignment)
        {
            var watermarks = _consumer.QueryWatermarkOffsets(partition, timeout);

            // Where this consumer will read next: its position once it has consumed here; before
            // that, the group's committed offset - a restarted consumer with nothing new to read has
            // no position at all; and with neither, where the reset policy starts it. The low
            // watermark matters on a compacted topic, whose first offset need not be 0.
            var next = _consumer.Position(partition);
            if (next == Offset.Unset)
            {
                next = committed.GetValueOrDefault(partition, Offset.Unset);
            }

            if (next == Offset.Unset)
            {
                next = _resetToLatest ? watermarks.High : watermarks.Low;
            }

            if (next.Value < watermarks.High.Value)
            {
                return false;
            }
        }

        return true;
    }

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

        // §6, numerically.
        MaxPollIntervalMs = 300_000,
        SessionTimeoutMs = 45_000,
        HeartbeatIntervalMs = 3_000,
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

        if (record.Message.Value is null)
        {
            if (_tombstoneHandler is not null)
            {
                await HandleAsync(record, _tombstoneHandler, cancellationToken);
            }
            else
            {
                // Not JSON at all, on a topic whose contract has no use for a null: first attempt (§9).
                await DeadLetterAsync(record, RejectionClass.Unparseable,
                    "null value: a tombstone on a topic whose register entry allows none", attempts: 1, _time.GetUtcNow());
            }
        }
        else
        {
            var verdict = _schema.Validate(record.Message.Value);
            if (!verdict.Valid)
            {
                // First attempt, no retry: a structurally invalid record cannot become valid (§9).
                await DeadLetterAsync(record, verdict.Rejection!.Value, verdict.Reason, attempts: 1, _time.GetUtcNow());
            }
            else
            {
                await HandleAsync(record, _handler, cancellationToken);
            }
        }

        _consumer.Commit(record);
        Processed++;
        Measure(record);
        return true;
    }

    /// <summary>Lag and record age are measured on settled records only: a record in flight is not yet read.</summary>
    private void Measure(ConsumeResult<string, byte[]> record)
    {
        _newest[record.TopicPartition] = record.Message.Timestamp.UtcDateTime;

        // The high watermark cached from the last fetch; no extra round trip per record.
        var watermarks = _consumer.GetWatermarkOffsets(record.TopicPartition);
        if (watermarks.High != Offset.Unset)
        {
            _lag[record.TopicPartition] = Math.Max(0, watermarks.High.Value - (record.Offset.Value + 1));
        }
    }

    private long _lastCaughtUpTicks;

    /// <summary>
    /// When this consumer last found itself <see cref="IsCaughtUp">caught up</see>, checked by its own
    /// loop after a poll that returned nothing; <c>null</c> until it first is. Readable from any
    /// thread: the readiness gate and the staleness header (<c>OD-018</c>) are built on it.
    /// </summary>
    public DateTimeOffset? LastCaughtUpUtc
    {
        get
        {
            var ticks = Interlocked.Read(ref _lastCaughtUpTicks);
            return ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    /// <summary>
    /// The consumer's own loop. A consumer is polled by one thread only — this one — so the
    /// caught-up check, which asks the client for its assignment and positions, runs here too.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (!await ProcessOneAsync(TimeSpan.FromMilliseconds(500), cancellationToken) && IsCaughtUp(TimeSpan.FromSeconds(5)))
            {
                Interlocked.Exchange(ref _lastCaughtUpTicks, _time.GetUtcNow().UtcTicks);
            }
        }
    }

    private async Task HandleAsync(
        ConsumeResult<string, byte[]> record, Func<ConsumeResult<string, byte[]>, CancellationToken, Task> handler, CancellationToken cancellationToken)
    {
        DateTimeOffset? firstFailure = null;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await handler(record, cancellationToken);
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
        _deadLetteredBy[rejection] = _deadLetteredBy.GetValueOrDefault(rejection) + 1;
    }

    public void Dispose()
    {
        _consumer.Close();
        _consumer.Dispose();
    }
}
