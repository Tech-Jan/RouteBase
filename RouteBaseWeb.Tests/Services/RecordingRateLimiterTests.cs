using RouteBaseWeb.Services;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// RC-02: the rate limiter uses the server's receipt clock, injected here as a fake so the window can
/// be advanced deterministically. A batch's own (possibly skewed) GPS timestamps never enter this path.
/// </summary>
public class RecordingRateLimiterTests
{
    private static (RecordingRateLimiter limiter, Action<double> advance) Build()
    {
        var now = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var limiter = new RecordingRateLimiter(() => now);
        return (limiter, seconds => now = now.AddSeconds(seconds));
    }

    // IsLimited/MarkAccepted were fused into the atomic TryAcquire (RS-04); these tests preserve the
    // original semantics (check-then-mark) against the new single method.
    [Fact]
    public void TryAcquire_UnknownSession_Succeeds()
    {
        var (limiter, _) = Build();
        Assert.True(limiter.TryAcquire("s1"));
    }

    [Fact]
    public void TryAcquire_SecondCallWithinWindow_False()
    {
        var (limiter, advance) = Build();
        Assert.True(limiter.TryAcquire("s1"));
        advance(0.5);
        Assert.False(limiter.TryAcquire("s1"));
    }

    [Fact]
    public void TryAcquire_AfterWindow_True()
    {
        var (limiter, advance) = Build();
        Assert.True(limiter.TryAcquire("s1"));
        advance(1.1);
        Assert.True(limiter.TryAcquire("s1"));
    }

    [Fact]
    public void TryAcquire_AfterClear_True()
    {
        var (limiter, _) = Build();
        Assert.True(limiter.TryAcquire("s1"));
        limiter.Clear("s1");
        Assert.True(limiter.TryAcquire("s1"));
    }

    [Fact]
    public void TryAcquire_Concurrent_OnlyOneWins()
    {
        // Real clock so all 8 threads see the same window; exactly one reservation must succeed.
        var limiter = new RecordingRateLimiter(() => DateTime.UtcNow);
        int wins = 0;
        Parallel.For(0, 8, _ =>
        {
            if (limiter.TryAcquire("race")) Interlocked.Increment(ref wins);
        });
        Assert.Equal(1, wins);
    }
}
