using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Tracks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace RouteBaseWeb.Services;

/// <summary>
/// Raw GPS coordinate access for activities. All methods verify visibility first — the GPS table
/// has no user_id of its own. Point data comes from the shared <see cref="ITrackReader"/> (one decode
/// per track, LRU-cached); downsampling is a stride over the decoded arrays. Concurrent identical
/// requests are coalesced by <see cref="GpsCoordinateCoalescer"/> so the shaping + cache-set runs
/// once — this class answers the request-scoped questions (visibility, owner, cache options) and
/// hands the shared work over; it never runs shared work itself, because a coalesced execution can
/// outlive the request and this service's <see cref="RouteBaseDbContext"/> dies with it.
/// </summary>
public class ActivityGpsService : IActivityGpsService
{
    private readonly RouteBaseDbContext _context;
    private readonly GpsCoordinateCache _gpsCache;
    private readonly IVisibilityService _visibility;
    private readonly UserCacheInvalidator _invalidator;
    private readonly GpsCoordinateCoalescer _coalescer;
    private readonly ITrackReader _reader;

    private static readonly TimeSpan BulkTrackCacheTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SingleTrackCacheTtl = TimeSpan.FromMinutes(10);

    public ActivityGpsService(
        RouteBaseDbContext context, GpsCoordinateCache gpsCache, IVisibilityService visibility,
        UserCacheInvalidator invalidator, GpsCoordinateCoalescer coalescer, ITrackReader reader)
    {
        _context     = context;
        _gpsCache    = gpsCache;
        _visibility  = visibility;
        _invalidator = invalidator;
        _coalescer   = coalescer;
        _reader      = reader;
    }

    // Cache options keyed on the viewer's token AND each data owner's token, so an owner-side
    // update/visibility change evicts every viewer's cached copy, not just the owner's own (AS-01).
    private MemoryCacheEntryOptions CacheOpts(TimeSpan ttl, IReadOnlyCollection<string> ownerIds)
    {
        var viewerId = _visibility.GetCurrentUserId();
        var opts = new MemoryCacheEntryOptions()
            .SetSlidingExpiration(ttl)
            .AddExpirationToken(_invalidator.GetChangeToken(viewerId));

        foreach (var ownerId in ownerIds)
            if (ownerId != viewerId)
                opts.AddExpirationToken(_invalidator.GetChangeToken(ownerId));

        return opts;
    }

    // Get GPS data for a track — verify ownership first (GPS table has no user_id)
    public async Task<List<GpsData>> GetGpsDataForTrackAsync(TrackId trackId, CancellationToken ct = default)
    {
        var owned = await _visibility.VisibleActivities(_context.Activities)
            .AnyAsync(a => a.TrackId == trackId, ct);
        if (!owned)
            return new List<GpsData>();

        var track = await _reader.GetTrackAsync(trackId, ct);
        return track == null ? new List<GpsData>() : track.ToGpsDataList(trackId);
    }

    /// <inheritdoc />
    public async Task<List<GpsData>> GetOwnedGpsDataForTrackAsync(TrackId trackId, CancellationToken ct = default)
    {
        // OwnedActivities, not VisibleActivities: this read exists precisely for the states the visible gate
        // hides (awaiting review, trashed). Ownership is the only gate — the caller decides which states it serves.
        var owned = await _visibility.OwnedActivities(_context.Activities)
            .AnyAsync(a => a.TrackId == trackId, ct);
        if (!owned)
            return new List<GpsData>();

        var track = await _reader.GetTrackAsync(trackId, ct);
        return track == null ? new List<GpsData>() : track.ToGpsDataList(trackId);
    }

    // Get track coordinates (lat/lng only, for minimap or full-track overlay)
    public async Task<List<GpsCoordinate>> GetTrackCoordinatesAsync(TrackId trackId, CancellationToken ct = default)
    {
        var userId   = _visibility.GetCurrentUserId();
        var cacheKey = CacheKeys.TrackCoordinates(userId, trackId);
        if (_gpsCache.TryGetValue(cacheKey, out List<GpsCoordinate>? cached) && cached != null)
            return cached;

        // Resolve the owner before the point read (GPS table has no user_id); null → not visible.
        var ownerId = await _visibility.VisibleActivities(_context.Activities)
            .Where(a => a.TrackId == trackId)
            .Select(a => a.UserId)
            .FirstOrDefaultAsync(ct);
        if (ownerId == null)
            return new List<GpsCoordinate>();

        var track  = await _reader.GetTrackAsync(trackId, ct);
        var result = TrackCoordinates.NullToZero(track);

        _gpsCache.Set(cacheKey, result, CacheOpts(SingleTrackCacheTtl, new[] { ownerId }).SetSize(result.Count));
        return result;
    }

    // Get track coordinates with downsampling (for progressive quality map overlays)
    public async Task<List<GpsCoordinate>> GetTrackCoordinatesAsync(TrackId trackId, int maxPoints, CancellationToken ct = default)
    {
        var userId   = _visibility.GetCurrentUserId();
        var cacheKey = CacheKeys.TrackCoordinatesDownsampled(userId, trackId, maxPoints);
        if (_gpsCache.TryGetValue(cacheKey, out List<GpsCoordinate>? cached) && cached != null)
            return cached;

        // Resolve the owner before the point read (GPS table has no user_id); null → not visible.
        var ownerId = await _visibility.VisibleActivities(_context.Activities)
            .Where(a => a.TrackId == trackId)
            .Select(a => a.UserId)
            .FirstOrDefaultAsync(ct);
        if (ownerId == null)
            return new List<GpsCoordinate>();

        // No limit — delegate to the no-maxPoints overload (cached under its own key there; caching the
        // same list again under this key would double-count it against the cache size limit). TE-06.
        if (maxPoints <= 0)
            return await GetTrackCoordinatesAsync(trackId, ct);

        // Visibility and options are resolved here, on the scoped context, BEFORE handing the decode +
        // stride + cache-set to the coalescer — that work is shared and can outlive this request.
        var opts = CacheOpts(SingleTrackCacheTtl, new[] { ownerId });
        return await _coalescer.ShapeAsync(cacheKey, trackId, maxPoints, opts, ct);
    }

    /// <inheritdoc />
    public async Task<List<GpsCoordinate>> GetOwnedTrackCoordinatesAsync(
        TrackId trackId, int maxPoints, CancellationToken ct = default)
    {
        var userId   = _visibility.GetCurrentUserId();
        var cacheKey = CacheKeys.TrackCoordinatesOwned(userId, trackId, maxPoints);
        if (_gpsCache.TryGetValue(cacheKey, out List<GpsCoordinate>? cached) && cached != null)
            return cached;

        // OwnedActivities, not VisibleActivities: this read exists precisely for the states the visible gate
        // hides (trashed). Ownership is the only gate here — the caller decides which states it will serve.
        var ownerId = await _visibility.OwnedActivities(_context.Activities)
            .Where(a => a.TrackId == trackId)
            .Select(a => a.UserId)
            .FirstOrDefaultAsync(ct);
        if (ownerId == null)
            return new List<GpsCoordinate>();

        var opts = CacheOpts(SingleTrackCacheTtl, new[] { ownerId });
        return await _coalescer.ShapeAsync(cacheKey, trackId, maxPoints, opts, ct);
    }

    /// <inheritdoc />
    public async Task<Dictionary<int, List<GpsCoordinate>>> GetBulkTrackCoordinatesAsync(
        List<int> trackIds, int maxPointsPerTrack, CancellationToken ct = default)
    {
        if (trackIds.Count == 0)
            return new Dictionary<int, List<GpsCoordinate>>();

        // Filter to only track IDs the current user can see (GPS table has no user_id); carry the
        // owner id so the cache entry can be tokened to every owner in the set (AS-01).
        var wantedIds = trackIds.Select(i => new TrackId(i)).ToList();
        var visible = await _visibility.VisibleActivities(_context.Activities)
            .Where(a => wantedIds.Contains(a.TrackId))
            .Select(a => new { a.TrackId, a.UserId })
            .ToListAsync(ct);

        if (visible.Count == 0)
            return new Dictionary<int, List<GpsCoordinate>>();

        var sortedIds = visible.Select(v => v.TrackId).Order().ToList();
        var owners    = visible.Select(v => v.UserId).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var userId    = _visibility.GetCurrentUserId();
        var cacheKey  = CacheKeys.BulkTracks(userId, sortedIds, maxPointsPerTrack);

        if (_gpsCache.TryGetValue(cacheKey, out Dictionary<int, List<GpsCoordinate>>? cachedResult) && cachedResult != null)
            return cachedResult;

        // Options are computed here (they read the scoped visibility service); the coalesced execution
        // writes the singleton cache with them, so it stays valid even if this request aborts while
        // other waiters are still attached.
        var opts = CacheOpts(BulkTrackCacheTtl, owners);
        return await _coalescer.BulkAsync(cacheKey, sortedIds, maxPointsPerTrack, opts, ct);
    }
}
