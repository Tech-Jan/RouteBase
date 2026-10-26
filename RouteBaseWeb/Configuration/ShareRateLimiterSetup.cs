using System.Threading.RateLimiting;
using RouteBaseWeb.Auth;
using RouteBaseWeb.Services.Sharing;

namespace RouteBaseWeb.Configuration;

/// <summary>
/// Share-link rate limiting. The first rate limiter in this app — nothing else here is rate-limited.
/// Moved whole from Program.cs (A9), rationale and all; ShareRateLimiterSetupTests pins the three
/// invariants documented inline (no IP-keyed partition, no cross-token ceiling, 404 rejection).
/// </summary>
public static class ShareRateLimiterSetup
{
    /// <summary>
    /// The one POLLING consumer on the share surface: share-live-view.ts hits
    /// <c>/s/{token}/api/recording/live</c> every 4 s per open viewer (15 req/min each), so the generic
    /// per-token budget below (60/min sustained) would go dark at ~4 concurrent viewers — and, because
    /// rejection is a 404, every viewer would read the frozen map as a dead link. The poll therefore gets
    /// its own per-token bucket, sized for viewers × cadence (~40 viewers sustained) — still bounded per
    /// token, still under the tier-2 global concurrency cap.
    /// </summary>
    public const string LivePollPathSuffix = "/api/recording/live";
    private const int LivePollTokenLimit = 600;
    private const int LivePollTokensPerMinute = 600;

    public static IServiceCollection AddShareRateLimiter(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            // 404, not 429: a prober learns nothing from a 404, and it matches the slice's uniform
            // "invalid is indistinguishable from a wrong URL" story. A 429 would confirm the URL shape is real.
            options.RejectionStatusCode = StatusCodes.Status404NotFound;

            // Rejections carry the share hygiene headers too ("every response, including
            // failures") — above all no-store, so a briefly throttled live link's 404 can never be cached by
            // a shared cache and read as permanently broken.
            options.OnRejected = (ctx, _) =>
            {
                if (IsShareRequest(ctx.HttpContext))
                    RouteBaseWeb.Services.Sharing.ShareResponseHeaders.Apply(ctx.HttpContext.Response);
                return ValueTask.CompletedTask;
            };

            // A GlobalLimiter rather than a named policy + [EnableRateLimiting] attribute, on purpose: it keys off
            // the presence of the share-token ROUTE VALUE, so every share endpoint is covered the moment it exists
            // and a new one cannot be shipped with the limiter forgotten. Non-share requests hit GetNoLimiter and
            // are untouched — this does not rate-limit the rest of the app.
            //
            // Chained: BOTH tiers must admit the request.
            //
            // A NOTE ON THE SHAPE SPLIT, because it is the whole design. An earlier version put ALL /s/ traffic
            // through one shared fixed window of 600/min. That window was reachable without a valid token — the
            // /s/{shareToken} route has no constraint, so `while true; do curl /s/x; done` at ~10 req/s starved it
            // and every legitimate share link in the deployment then 404'd. Because rejection is 404 (below),
            // owner and recipient both read that as "the link is broken" and no 429 anywhere says otherwise.
            // Shared fate across unrelated tokens was the bug; splitting probes from real traffic is the fix.
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(

                // Tier 1 — SHAPE triage. A token that fails IsWellFormed cannot possibly resolve, and no
                // legitimate visitor ever produces one, so garbage gets its own tight bucket that real traffic
                // never draws from. This is a pure string check: no DB, no hash, no allocation worth the name.
                PartitionedRateLimiter.Create<HttpContext, string>(context =>
                {
                    var token = ShareRouteToken(context);
                    if (token is null) return RateLimitPartition.GetNoLimiter("not-share");
                    if (ShareTokens.IsWellFormed(token)) return RateLimitPartition.GetNoLimiter("share:well-formed");

                    return RateLimitPartition.GetFixedWindowLimiter("share:malformed", _ =>
                        new FixedWindowRateLimiterOptions
                        {
                            PermitLimit = 60,
                            Window = TimeSpan.FromMinutes(1),
                            QueueLimit = 0,
                        });
                }),

                // Tier 2 — global CONCURRENCY across all share traffic, not a request count. This is what bounds
                // the cost of guessing well-formed tokens (an attacker can still generate those at will), and a
                // concurrency limiter is the right instrument: it caps in-flight DB lookups without ever putting
                // the feature into a "denied for the rest of the window" state. Over the limit, requests QUEUE
                // briefly rather than fail, so a flood slows legitimate viewers instead of locking them out.
                PartitionedRateLimiter.Create<HttpContext, string>(context =>
                    !IsShareRequest(context)
                        ? RateLimitPartition.GetNoLimiter("not-share")
                        : RateLimitPartition.GetConcurrencyLimiter("share:inflight", _ =>
                            new ConcurrencyLimiterOptions
                            {
                                PermitLimit = 32,
                                QueueLimit = 64,
                                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                            })),

                // Tier 3 — per-token bucket. Bounds the damage when a REAL link leaks and gets hammered; it is not
                // an enumeration defence (see tiers 1-2). Partitioned on the token HASH, never the plaintext:
                // partition keys sit in in-memory dictionaries and can surface in diagnostics.
                //
                // NO tier partitions on IP. UseForwardedHeaders trusts X-Forwarded-For from any upstream
                // (KnownProxies/KnownNetworks are cleared), so RemoteIpAddress is attacker-controlled. An IP
                // partition would not merely fail to help — it would let an attacker exhaust a chosen victim's
                // bucket by spoofing their address.
                PartitionedRateLimiter.Create<HttpContext, string>(context =>
                {
                    if (!IsShareRequest(context)) return RateLimitPartition.GetNoLimiter("not-share");

                    // The live poll is a steady per-viewer drip, not a page load — its own partition (and
                    // budget) per token, so viewers of a live map never drain the page/photo bucket and
                    // vice versa. See LivePollPathSuffix.
                    if (context.Request.Path.Value?.EndsWith(LivePollPathSuffix, StringComparison.OrdinalIgnoreCase) == true)
                        return RateLimitPartition.GetTokenBucketLimiter(
                            "share-live:" + ShareTokens.HashOrEmpty(ShareRouteToken(context)), _ =>
                                new TokenBucketRateLimiterOptions
                                {
                                    TokenLimit = LivePollTokenLimit,
                                    TokensPerPeriod = LivePollTokensPerMinute,
                                    ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                                    QueueLimit = 0,
                                    AutoReplenishment = true,
                                });

                    return RateLimitPartition.GetTokenBucketLimiter(
                        "share:" + ShareTokens.HashOrEmpty(ShareRouteToken(context)), _ =>
                            new TokenBucketRateLimiterOptions
                            {
                                // One share page load fans out to several subresource requests (photos,
                                // tiles), so the burst allowance must cover a page plus a refresh or two.
                                TokenLimit = 120,
                                TokensPerPeriod = 60,
                                ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                                QueueLimit = 0,
                                AutoReplenishment = true,
                            });
                }));

            static string? ShareRouteToken(HttpContext context)
                => context.Request.RouteValues[ShareLinkDefaults.RouteTokenKey] as string;

            static bool IsShareRequest(HttpContext context) => ShareRouteToken(context) is not null;
        });
        return services;
    }
}
