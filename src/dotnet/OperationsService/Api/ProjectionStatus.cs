using Confluent.Kafka;
using Mair.OperationsService.Projection;

namespace Mair.OperationsService.Api;

/// <summary>What the API needs to know about the projector, read from the API's own threads.</summary>
public interface IProjectionStatus
{
    /// <summary>When the consumer of <paramref name="topic"/> was last caught up (OD-018); <c>null</c> if never.</summary>
    DateTimeOffset? LastCaughtUp(string topic);

    /// <summary>Whether that consumer is caught up now: its latest check said so, and nothing has arrived since.</summary>
    bool CaughtUp(string topic);

    /// <summary>The largest per-partition lag across the projector's consumers.</summary>
    long MaxLag { get; }

    /// <summary>The prediction consumer's largest per-partition record age; <c>null</c> before any.</summary>
    TimeSpan? PredictionRecordAge { get; }
}

/// <summary>The running projector's status, from the thread-safe snapshots its consumers publish.</summary>
public sealed class ProjectorStatus(OperationsProjector projector) : IProjectionStatus
{
    public DateTimeOffset? LastCaughtUp(string topic) => projector.Consumers.Single(c => c.Topic == topic).LastCaughtUpUtc;

    public bool CaughtUp(string topic) => projector.Consumers.Single(c => c.Topic == topic).CaughtUp;

    public long MaxLag => projector.Consumers.Max(c => c.MaxLag);

    public TimeSpan? PredictionRecordAge => projector.Consumers.Single(c => c.Topic == "factory.predictions.v1").MaxRecordAge;
}

/// <summary>Whether the Kafka cluster answers — a real request, not a guess from projector freshness.</summary>
public interface IKafkaProbe
{
    bool Reachable();
}

/// <summary>A metadata request with a 2 s timeout, its answer cached for 5 s (<c>OPERATIONS_API.md</c> §10a).</summary>
public sealed class KafkaProbe(IAdminClient admin, TimeProvider time) : IKafkaProbe
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(5);
    private readonly Lock _gate = new();
    private DateTimeOffset _checkedAt = DateTimeOffset.MinValue;
    private bool _reachable;

    public bool Reachable()
    {
        lock (_gate)
        {
            var now = time.GetUtcNow();
            if (now - _checkedAt >= CacheFor)
            {
                try
                {
                    _reachable = admin.GetMetadata(TimeSpan.FromSeconds(2)).Brokers.Count > 0;
                }
                catch (KafkaException)
                {
                    _reachable = false;
                }

                _checkedAt = now;
            }

            return _reachable;
        }
    }
}
