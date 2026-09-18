namespace Mair.EdgeGateway;

/// <summary>
/// Reconnect backoff for an OT endpoint: <c>250 ms → 8 s, exponential ×2, ±20 % jitter</c>
/// (`OT_PROTOCOL_MAPPING.md` §2.7, `EDGE_GATEWAY.md` §11).
/// <para>
/// The ceiling matters more than the growth. <b>Reconnect is infinite</b> — the gateway never gives
/// up on equipment — so an unbounded backoff would eventually mean an endpoint that came back was
/// not noticed for hours, and R-01 requires recovery within 10 s of the endpoint being restored.
/// </para>
/// <para>
/// The jitter is not decoration. At demo scale twenty equipment lose a shared endpoint at the same
/// instant, and without jitter all twenty would retry on exactly the same schedule forever,
/// converting a recovery into a thundering herd against a server that is still starting.
/// </para>
/// </summary>
public sealed class ReconnectBackoff
{
    public static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan MaxDelay = TimeSpan.FromSeconds(8);
    public const double JitterFraction = 0.20;

    private readonly Func<double> _jitter;
    private int _attempt;

    /// <param name="jitter">
    /// Returns a value in [0,1), used to place the delay inside its ±20 % band. Injected so a test
    /// can pin the extremes rather than sampling and hoping.
    /// </param>
    public ReconnectBackoff(Func<double>? jitter = null)
    {
        var random = new Random();
        _jitter = jitter ?? random.NextDouble;
    }

    /// <summary>Consecutive failed attempts since the last <see cref="Reset"/>.</summary>
    public int Attempt => _attempt;

    /// <summary>Called on a successful connect. The next outage starts from 250 ms again.</summary>
    public void Reset() => _attempt = 0;

    /// <summary>The delay before the next attempt, and advances the sequence.</summary>
    public TimeSpan Next()
    {
        // 250, 500, 1000, 2000, 4000, 8000, 8000, ... Shifting rather than multiplying keeps the
        // base exact, and the exponent is capped before the shift so a long outage cannot overflow
        // into a negative delay - which is how a "retry forever" loop turns into a busy loop.
        var steps = Math.Min(_attempt, 5);
        var baseMs = InitialDelay.TotalMilliseconds * (1 << steps);
        baseMs = Math.Min(baseMs, MaxDelay.TotalMilliseconds);

        _attempt++;

        // ±20 % around the base: jitter 0 gives -20 %, 1 gives +20 %.
        var factor = 1.0 - JitterFraction + (2 * JitterFraction * _jitter());
        return TimeSpan.FromMilliseconds(baseMs * factor);
    }
}
