using RouteBaseWeb.Services.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services;

/// <summary>
/// Shared cache-invalidation collaborator for the segment services. Owns the per-segment derived-data
/// key set and the change-token-backed entry options so the split services stay cache-coherent:
/// <see cref="SegmentEfforts"/> writes the derived entries, and <see cref="SegmentCrud"/>'s delete/update
/// must evict them (Crud's delete still reaches Efforts' cache keys). Scoped: it reads the current user
/// via <see cref="IVisibilityService"/>.
/// </summary>
public sealed class SegmentCache
{
    private readonly IMemoryCache _cache;
    private readonly IVisibilityService _visibility;
    private readonly UserCacheInvalidator _invalidator;

    public SegmentCache(IMemoryCache cache, IVisibilityService visibility, UserCacheInvalidator invalidator)
    {
        _cache       = cache;
        _visibility  = visibility;
        _invalidator = invalidator;
    }

    // Cache TTLs — keep in sync with the Caching Strategy table in ARCHITECTURE.md.
    // Short (5 min): derived data that is invalidated by analysis runs (efforts, performance, GPS per-effort).
    // Long  (10 min): stable GPS geometry that only changes on delete (segment GPS, effort GPS slices).
    public static readonly TimeSpan ShortTtl = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan LongTtl  = TimeSpan.FromMinutes(10);

    /// <summary>Absolute ceiling so no entry outlives this regardless of sliding-renewal access pattern
    /// (SA-07: a viewer polling faster than the sliding TTL could otherwise serve stale data forever).</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Entry options: sliding TTL + absolute ceiling + the viewer's change token, plus the data owner's
    /// change token when <paramref name="ownerUserId"/> differs from the viewer. Owner-tagging (AS-01
    /// pattern) is what lets an owner-side mutation — delete / re-analyze / effort removal, which trips
    /// the owner's token via <see cref="InvalidateUser"/> — evict every viewer's cached copy, not just
    /// the owner's own keys.
    /// </summary>
    public MemoryCacheEntryOptions Options(TimeSpan ttl, string? ownerUserId = null)
    {
        var userId = _visibility.GetCurrentUserId();
        var opts = new MemoryCacheEntryOptions()
            .SetSlidingExpiration(ttl)
            .SetAbsoluteExpiration(MaxLifetime)
            .AddExpirationToken(_invalidator.GetChangeToken(userId));

        if (ownerUserId != null && ownerUserId != userId)
            opts.AddExpirationToken(_invalidator.GetChangeToken(ownerUserId));

        return opts;
    }

    /// <summary>Options for a user-INDEPENDENT shared entry (e.g. the SegmentReference polyline): sliding
    /// TTL + absolute ceiling, but NO change token — SA-12: tagging it with the populating viewer's token
    /// evicts the shared entry for everyone when that one viewer is invalidated. Identity-change eviction
    /// (delete + id-reuse) goes through <see cref="InvalidateSegment"/>'s explicit Remove instead.</summary>
    public MemoryCacheEntryOptions SharedOptions(TimeSpan ttl) =>
        new MemoryCacheEntryOptions()
            .SetSlidingExpiration(ttl)
            .SetAbsoluteExpiration(MaxLifetime);

    /// <summary>
    /// Options for a SHARE-path entry: the data owner's change token only. The viewer is deliberately not
    /// consulted — a share viewer is the constant <c>share:anon</c>, so tagging with its token would put
    /// every live share link in one blast radius that nothing ever trips.
    /// </summary>
    public MemoryCacheEntryOptions OwnerTokenOptions(TimeSpan ttl, string ownerUserId) =>
        new MemoryCacheEntryOptions()
            .SetSlidingExpiration(ttl)
            .SetAbsoluteExpiration(MaxLifetime)
            .AddExpirationToken(_invalidator.GetChangeToken(ownerUserId));

    /// <summary>Evicts every per-segment derived cache entry for a user (efforts, performance, GPS, totals).</summary>
    public void InvalidateSegment(string userId, SegmentId segmentId)
    {
        _cache.Remove(CacheKeys.SegmentEfforts(userId, segmentId));
        _cache.Remove(CacheKeys.SegmentPerformance(userId, segmentId));
        _cache.Remove(CacheKeys.SegmentEffortsGps(userId, segmentId));
        _cache.Remove(CacheKeys.SegmentEffortTotals(userId, segmentId));
        // Share-path entries for the SAME segment. Both call sites of this method sit behind an
        // OwnedSegments gate, so `userId` is the segment owner — which is exactly how the share keys are
        // keyed. Without these two lines, an owner excluding an effort would leave every live share link
        // serving the pre-exclusion leaderboard until TTL.
        _cache.Remove(CacheKeys.ShareSegmentEfforts(userId, segmentId));
        _cache.Remove(CacheKeys.ShareSegmentTotals(userId, segmentId));
        // SE-03: the shared (un-user-scoped) reference polyline is keyed by segmentId only; without this
        // a delete + SQLite id-reuse would serve the old segment's geometry as the new segment's chainage.
        _cache.Remove(CacheKeys.SegmentReference(segmentId));
    }

    /// <summary>Trips the user's change token, evicting every entry tagged with it (e.g. per-maxPoints
    /// GPS-summary keys that <see cref="InvalidateSegment"/> can't target individually).</summary>
    public void InvalidateUser(string userId) => _invalidator.InvalidateUser(userId);
}
