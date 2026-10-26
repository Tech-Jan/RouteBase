using System.Net;
using System.Threading.RateLimiting;
using RouteBaseWeb.Auth;
using RouteBaseWeb.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace RouteBaseWeb.Tests.Configuration;

/// <summary>
/// Pins the share rate limiter's three documented invariants (Configuration/ShareRateLimiterSetup.cs):
/// (a) no partition is keyed on client IP — RemoteIpAddress is attacker-controlled behind the
///     cleared-KnownProxies forwarder, so an IP key would let a spoofer drain a victim's bucket;
/// (b) no request-count ceiling is shared across tokens — the P2 DoS fix: a flood on token X (or on
///     malformed garbage) must never starve token Y;
/// (c) rejection is 404, not 429 — a prober must not learn that the URL shape is real.
/// Each test builds a fresh limiter so buckets start full.
/// </summary>
public class ShareRateLimiterSetupTests
{
    /// <summary>Tier-3 burst size (TokenLimit) — drained fully, the next acquire is denied.</summary>
    private const int PerTokenBurst = 120;

    /// <summary>Tier-1 malformed-probe window (PermitLimit/min).</summary>
    private const int MalformedWindow = 60;

    private static readonly string TokenA = new('a', 43);
    private static readonly string TokenB = new('b', 43);

    private static RateLimiterOptions BuildOptions()
    {
        var services = new ServiceCollection().AddSingleton(TimeProvider.System);
        services.AddShareRateLimiter();
        return services.BuildServiceProvider().GetRequiredService<IOptions<RateLimiterOptions>>().Value;
    }

    private static PartitionedRateLimiter<HttpContext> BuildLimiter() =>
        BuildOptions().GlobalLimiter!;

    private static DefaultHttpContext Context(string? token, string ip = "203.0.113.10")
    {
        var context = new DefaultHttpContext();
        if (token is not null)
            context.Request.RouteValues[ShareLinkDefaults.RouteTokenKey] = token;
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        return context;
    }

    /// <summary>A live-map poll request: same token route value, but on the polling path.</summary>
    private static DefaultHttpContext LivePollContext(string token)
    {
        var context = Context(token);
        context.Request.Path = "/s/" + token + ShareRateLimiterSetup.LivePollPathSuffix;
        return context;
    }

    private static void Drain(PartitionedRateLimiter<HttpContext> limiter, string token, int count, string ip)
    {
        for (var i = 0; i < count; i++)
        {
            // Dispose per acquire: frees the tier-2 concurrency permit (leases return), while the
            // tier-3 token-bucket permit stays consumed (token buckets refund by replenishment only).
            using var lease = limiter.AttemptAcquire(Context(token, ip));
            Assert.True(lease.IsAcquired, $"acquire #{i + 1} of {count} unexpectedly denied");
        }
    }

    // ── (c) rejection surface ───────────────────────────────────────────────────

    [Fact]
    public void Rejection_Is404_Not429() =>
        Assert.Equal(StatusCodes.Status404NotFound, BuildOptions().RejectionStatusCode);

    [Fact]
    public async Task Rejection_CarriesShareHygieneHeaders()
    {
        var options = BuildOptions();
        var limiter = options.GlobalLimiter!;
        Drain(limiter, TokenA, PerTokenBurst, ip: "203.0.113.10");
        using var denied = limiter.AttemptAcquire(Context(TokenA));
        Assert.False(denied.IsAcquired);

        var rejectedContext = Context(TokenA);
        await options.OnRejected!(
            new OnRejectedContext { HttpContext = rejectedContext, Lease = denied }, CancellationToken.None);

        // no-store above all: a throttled link's 404 must never be cached as "permanently broken".
        Assert.Equal("private, no-store, max-age=0", rejectedContext.Response.Headers.CacheControl);
        Assert.Equal("noindex, nofollow, noarchive, nosnippet",
            rejectedContext.Response.Headers["X-Robots-Tag"].ToString());
    }

    // ── (b) no cross-token ceiling ──────────────────────────────────────────────

    [Fact]
    public void FloodOnOneToken_DoesNotStarveAnotherToken()
    {
        var limiter = BuildLimiter();

        Drain(limiter, TokenA, PerTokenBurst, ip: "203.0.113.10");
        using var deniedA = limiter.AttemptAcquire(Context(TokenA));
        Assert.False(deniedA.IsAcquired); // the flooded token IS throttled…

        using var freshB = limiter.AttemptAcquire(Context(TokenB));
        Assert.True(freshB.IsAcquired); // …and an unrelated token is untouched.
    }

    [Fact]
    public void MalformedProbeFlood_DoesNotStarveRealTokens()
    {
        var limiter = BuildLimiter();

        Drain(limiter, "garbage", MalformedWindow, ip: "203.0.113.10");
        using var deniedProbe = limiter.AttemptAcquire(Context("garbage"));
        Assert.False(deniedProbe.IsAcquired); // tier 1 closed the probe bucket…

        using var real = limiter.AttemptAcquire(Context(TokenA));
        Assert.True(real.IsAcquired); // …without touching well-formed traffic.
    }

    // ── Live-poll partition (PI-02): polling viewers must not drain the page bucket ──

    [Fact]
    public void LivePoll_SurvivesTheGenericBurst_AndPageLoadsSurviveThePollDrip()
    {
        // 8 viewers polling every 4 s for a few minutes far exceed the generic 120 burst — the poll has its
        // own per-token bucket, so neither side starves the other.
        var limiter = BuildLimiter();

        for (var i = 0; i < PerTokenBurst + 60; i++)
        {
            using var poll = limiter.AttemptAcquire(LivePollContext(TokenA));
            Assert.True(poll.IsAcquired, $"live poll #{i + 1} unexpectedly denied");
        }

        using var page = limiter.AttemptAcquire(Context(TokenA));
        Assert.True(page.IsAcquired); // the page/photo bucket is untouched by the poll drip
    }

    [Fact]
    public void LivePollBucket_IsStillBounded_PerToken()
    {
        // A leaked live link can still be hammered — the poll partition is larger, not unlimited.
        var limiter = BuildLimiter();

        var admitted = 0;
        for (var i = 0; i < 1000; i++)
        {
            using var lease = limiter.AttemptAcquire(LivePollContext(TokenA));
            if (lease.IsAcquired) admitted++;
        }

        Assert.InRange(admitted, 1, 999);          // some got through, then the bucket closed…

        using var otherToken = limiter.AttemptAcquire(LivePollContext(TokenB));
        Assert.True(otherToken.IsAcquired);        // …without touching another token's viewers.
    }

    // ── (a) partitions never key on client IP ───────────────────────────────────

    [Fact]
    public void SameToken_FromDifferentClientIp_SharesTheSameBucket()
    {
        var limiter = BuildLimiter();

        Drain(limiter, TokenA, PerTokenBurst, ip: "203.0.113.10");

        // If any tier keyed on IP, a "new client" would get a fresh bucket — and, inverted, a spoofer
        // could drain a victim's. The deny must follow the token, not the address.
        using var denied = limiter.AttemptAcquire(Context(TokenA, ip: "198.51.100.99"));
        Assert.False(denied.IsAcquired);
    }

    // ── Non-share traffic is untouched ──────────────────────────────────────────

    [Fact]
    public void NonShareRequests_NeverThrottle()
    {
        var limiter = BuildLimiter();

        for (var i = 0; i < PerTokenBurst + MalformedWindow; i++)
        {
            using var lease = limiter.AttemptAcquire(Context(token: null));
            Assert.True(lease.IsAcquired);
        }
    }
}
