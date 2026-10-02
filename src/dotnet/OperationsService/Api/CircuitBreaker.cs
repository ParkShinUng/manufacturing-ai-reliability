namespace Mair.OperationsService.Api;

/// <summary>
/// The API's breaker on its database calls, per instance (<c>OD-022</c>, <c>OPERATIONAL_DATA.md</c>
/// §12): five consecutive unavailable results open it; for 30 s every database route answers
/// <c>503</c> at once; then one request is let through — success closes it, failure opens it for
/// another 30 s. Requests arriving while that probe is out get <c>503</c>.
/// </summary>
public sealed class CircuitBreaker(TimeProvider time)
{
    public const int Threshold = 5;
    public static readonly TimeSpan OpenFor = TimeSpan.FromSeconds(30);

    private readonly Lock _gate = new();
    private int _failures;
    private DateTimeOffset? _openUntil;
    private bool _probing;

    /// <returns><c>false</c> when the request must be refused without asking the store.</returns>
    public bool TryEnter()
    {
        lock (_gate)
        {
            if (_openUntil is null)
            {
                return true;
            }

            if (time.GetUtcNow() < _openUntil || _probing)
            {
                return false;
            }

            _probing = true;
            return true;
        }
    }

    public void Success()
    {
        lock (_gate)
        {
            _failures = 0;
            _openUntil = null;
            _probing = false;
        }
    }

    public void Failure()
    {
        lock (_gate)
        {
            _failures++;
            if (_probing || _failures >= Threshold)
            {
                _openUntil = time.GetUtcNow() + OpenFor;
            }

            _probing = false;
        }
    }

    /// <summary>The request ended without telling anything about the store; a probe slot is returned.</summary>
    public void Neutral()
    {
        lock (_gate)
        {
            _probing = false;
        }
    }
}
