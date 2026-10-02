using Mair.OperationsService.Api;

namespace Mair.OperationsService.Tests;

/// <summary>The API's breaker (OD-022), on a clock the test moves.</summary>
public sealed class CircuitBreakerTests
{
    private sealed class ManualClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void FiveConsecutiveFailuresOpenIt_ForThirtySeconds_ThenOneProbe()
    {
        var clock = new ManualClock();
        var breaker = new CircuitBreaker(clock);

        for (var i = 0; i < 4; i++)
        {
            Assert.True(breaker.TryEnter());
            breaker.Failure();
        }

        // A success resets the count: failures must be consecutive.
        Assert.True(breaker.TryEnter());
        breaker.Success();
        for (var i = 0; i < 5; i++)
        {
            Assert.True(breaker.TryEnter());
            breaker.Failure();
        }

        Assert.False(breaker.TryEnter());
        clock.Now += TimeSpan.FromSeconds(29);
        Assert.False(breaker.TryEnter());

        clock.Now += TimeSpan.FromSeconds(1);
        Assert.True(breaker.TryEnter());   // the probe
        Assert.False(breaker.TryEnter());  // anyone else while it is out
        breaker.Failure();                 // probe failed: open again
        Assert.False(breaker.TryEnter());

        clock.Now += CircuitBreaker.OpenFor;
        Assert.True(breaker.TryEnter());
        breaker.Success();                 // probe succeeded: closed
        Assert.True(breaker.TryEnter());
        Assert.True(breaker.TryEnter());
    }

    [Fact]
    public void ARequestThatNeverReachedTheStoreReturnsTheProbeSlot()
    {
        var clock = new ManualClock();
        var breaker = new CircuitBreaker(clock);
        for (var i = 0; i < CircuitBreaker.Threshold; i++)
        {
            breaker.TryEnter();
            breaker.Failure();
        }

        clock.Now += CircuitBreaker.OpenFor;
        Assert.True(breaker.TryEnter());
        breaker.Neutral();                 // e.g. a 400: says nothing about the store
        Assert.True(breaker.TryEnter());   // the next request may probe
    }
}
