using RouteBaseWeb.Helpers;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.GridFilter;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace RouteBaseWeb.Services;

/// <summary>
/// Read-only metadata queries, statistics, and the paged/filtered activities grid.
/// GPS coordinate access lives in <see cref="ActivityGpsService"/>; writes in
/// <see cref="ActivityCommandService"/>.
/// </summary>
public class ActivityQueryService : IActivityQueryService
{
    private readonly RouteBaseDbContext _context;
    private readonly IVisibilityService _visibility;
    private readonly IMemoryCache _cache;
    private readonly UserCacheInvalidator _invalidator;

    private static readonly TimeSpan SliceCacheTtl = TimeSpan.FromMinutes(5);

    public ActivityQueryService(
        RouteBaseDbContext context, IVisibilityService visibility,
        IMemoryCache cache, UserCacheInvalidator invalidator)
    {
        _context     = context;
        _visibility  = visibility;
        _cache       = cache;
        _invalidator = invalidator;
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

    // Get all activities. No MultiDayTour Include: the navigation is only read on the Details page
    // (GetActivityMetadataByIdAsync) — here it would LEFT JOIN + materialize a tour per row for nothing.
    public async Task<List<Activity>> GetAllActivitiesAsync()
    {
        return await _visibility.VisibleActivities(_context.Activities)
            .AsNoTracking()
            .OrderByDescending(a => a.StartDateTimeLocal)
            .ToListAsync();
    }

    // Get activity metadata by ID (no GPS data — lightweight)
    public async Task<Activity?> GetActivityMetadataByIdAsync(int id)
    {
        return await _visibility.VisibleActivities(_context.Activities)
            .AsNoTracking()
            .Include(a => a.MultiDayTour)
            .FirstOrDefaultAsync(a => a.TrackId == new TrackId(id));
    }

    /// <inheritdoc />
    public async Task<Activity?> GetOwnedActivityMetadataByIdAsync(int id)
    {
        return await _visibility.OwnedActivities(_context.Activities)
            .AsNoTracking()
            .Include(a => a.MultiDayTour)
            .FirstOrDefaultAsync(a => a.TrackId == new TrackId(id));
    }

    /// <inheritdoc />
    public async Task<bool> IsActivityOwnedAsync(TrackId trackId)
    {
        return await _visibility.OwnedActivities(_context.Activities)
            .AnyAsync(a => a.TrackId == trackId);
    }

    /// <inheritdoc />
    public async Task<bool> IsActivityVisibleAsync(TrackId trackId)
    {
        return await _visibility.VisibleActivities(_context.Activities)
            .AnyAsync(a => a.TrackId == trackId);
    }

    /// <inheritdoc />
    public async Task<Dictionary<int, ActivityMapSlice>> GetActivitySlicesByIdsAsync(List<int> trackIds, CancellationToken ct = default)
    {
        if (trackIds.Count == 0)
            return new Dictionary<int, ActivityMapSlice>();

        var userId   = _visibility.GetCurrentUserId();
        var cacheKey = CacheKeys.ActivitySlices(userId, trackIds);

        if (_cache.TryGetValue(cacheKey, out Dictionary<int, ActivityMapSlice>? cached) && cached != null)
            return cached;

        // Carry OwnerId alongside the client DTO (kept off ActivityMapSlice) so the cache entry
        // can be tokened to each owner without leaking user ids into the response.
        var wantedIds = trackIds.Select(i => new TrackId(i)).ToList();
        var rows = await _visibility.VisibleActivities(_context.Activities)
            .Where(a => wantedIds.Contains(a.TrackId))
            .Select(a => new
            {
                OwnerId = a.UserId,
                Slice = new ActivityMapSlice
                {
                    TrackId            = a.TrackId.Value,
                    TrackName          = a.TrackName,
                    ActivityType       = a.ActivityType,
                    StartDateTimeLocal = a.StartDateTimeLocal,
                    TotalDistance      = a.TotalDistance,
                    TotalAscent        = a.TotalAscent
                }
            })
            .ToListAsync(ct);

        var result = rows.ToDictionary(r => r.Slice.TrackId, r => r.Slice);
        var owners = rows.Select(r => r.OwnerId).OfType<string>().Distinct(StringComparer.Ordinal).ToList();

        _cache.Set(cacheKey, result, CacheOpts(SliceCacheTtl, owners));
        return result;
    }

    /// <inheritdoc />
    public async Task<List<ActivityEndpoint>> GetActivityEndpointsAsync(List<int> trackIds, CancellationToken ct = default)
    {
        if (trackIds.Count == 0)
            return new List<ActivityEndpoint>();

        // Cheap projection over the Activity table only — no GPS join. Used to frame the
        // initial map viewport before any track GPS is fetched.
        var wantedEndpointIds = trackIds.Select(i => new TrackId(i)).ToList();
        return await _visibility.VisibleActivities(_context.Activities)
            .Where(a => wantedEndpointIds.Contains(a.TrackId))
            .Select(a => new ActivityEndpoint
            {
                StartLat = a.StartGpsLat,
                StartLng = a.StartGpsLong,
                EndLat   = a.EndGpsLat,
                EndLng   = a.EndGpsLong,
            })
            .ToListAsync(ct);
    }

    // Get distinct activity types
    public async Task<List<string>> GetActivityTypesAsync()
    {
        return await _visibility.VisibleActivities(_context.Activities)
            .Where(a => a.ActivityType != null)
            .Select(a => a.ActivityType!)
            .Distinct()
            .OrderBy(t => t)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<List<int>> GetTrackIdsContainingPointAsync(double lat, double lng, CancellationToken ct = default)
    {
        // Coarse point-in-bbox pre-filter over the Activity_Bbox helper table (not EF-modelled). No
        // visibility predicate — the caller intersects the result with its own candidate set.
        return await _context.Database.SqlQueryRaw<int>(
            "SELECT track_ID AS Value FROM Activity_Bbox " +
            "WHERE min_lat <= {0} AND max_lat >= {0} AND min_long <= {1} AND max_long >= {1}", lat, lng)
            .ToListAsync(ct);
    }

    /// <inheritdoc />
    public Task<int> GetVisibleActivityCountAsync() =>
        _visibility.VisibleActivities(_context.Activities).CountAsync();

    /// <inheritdoc />
    public async Task<Dictionary<TrackId, int>> GetPhotoCountsAsync(IReadOnlyCollection<TrackId> trackIds, CancellationToken ct = default)
    {
        if (trackIds.Count == 0)
            return new Dictionary<TrackId, int>();

        // One indexed GROUP BY (IX_Photo_TrackId) over the page's ids — bounded by the page, never N+1.
        var ids = trackIds as List<TrackId> ?? trackIds.ToList();
        return await _context.Photos
            .Where(p => ids.Contains(p.TrackId))
            .GroupBy(p => p.TrackId)
            .Select(g => new { TrackId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TrackId, x => x.Count, ct);
    }

    // Get activities that have GPS coordinates (for segment creation page)
    public async Task<List<Activity>> GetActivitiesWithGpsCoordinatesAsync(int limit = 100)
    {
        return await _visibility.VisibleActivities(_context.Activities)
            .AsNoTracking()
            .Where(a => a.StartGpsLat != null && a.StartGpsLong != null)
            .OrderByDescending(a => a.StartDateTimeLocal)
            .Take(limit)
            .ToListAsync();
    }


    // Get filtered activity IDs
    /// <inheritdoc />
    public async Task<List<TrackId>> GetFilteredActivityIdsAsync(
        string? activityType,
        DateTime? startDate,
        DateTime? endDate,
        string? ownerUserId = null,
        IReadOnlyList<string>? activityTypes = null,
        GridFilterCollection? filters = null,
        IReadOnlyList<string>? ownerUserIds = null,
        bool ownerMatchNone = false,
        IReadOnlyCollection<TrackId>? restrictIds = null)
    {
        var query = _visibility.VisibleActivities(_context.Activities).AsQueryable();
        query = ApplyActivityFilters(query, activityType, startDate, endDate, ownerUserId, activityTypes, filters, ownerUserIds, ownerMatchNone, restrictIds);
        return await query.Select(a => a.TrackId).ToListAsync();
    }

    /// <inheritdoc />
    public async Task<(List<Activity> Items, int TotalCount)> GetPagedActivitiesAsync(
        string? activityType,
        DateTime? startDate,
        DateTime? endDate,
        string sortColumn,
        string sortDirection,
        int page,
        int pageSize,
        string? ownerUserId = null,
        IReadOnlyList<string>? activityTypes = null,
        GridFilterCollection? filters = null,
        IReadOnlyList<string>? ownerUserIds = null,
        bool ownerMatchNone = false,
        IReadOnlyCollection<TrackId>? restrictIds = null,
        int? knownCount = null)
    {
        var query = _visibility.VisibleActivities(_context.Activities).AsNoTracking();
        query = ApplyActivityFilters(query, activityType, startDate, endDate, ownerUserId, activityTypes, filters, ownerUserIds, ownerMatchNone, restrictIds);

        // The grid callers materialize the full filtered id set (GetFilteredActivityIdsAsync) with the SAME
        // predicates, so its .Count is this COUNT — reuse it instead of re-running the aggregate per interaction.
        var totalCount = knownCount ?? await query.CountAsync();

        // Sorting — done in DB to get correct pagination across large datasets. The column registry is the
        // allowlist: an unknown/non-sortable key falls back to the default (date).
        bool asc = string.Equals(sortDirection, SortDirections.Asc, StringComparison.OrdinalIgnoreCase);
        query = ActivityGridColumns.Instance.ApplySort(query, sortColumn, asc);

        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (items, totalCount);
    }

    // Filters a query by activity-type group ("bike", "run", "walk") or falls back to
    // an exact match for any other value passed. Mirrors ActivityTypeHelper.GetTypeGroup.
    private static IQueryable<Activity> ApplyTypeGroupFilter(IQueryable<Activity> query, string typeGroup) =>
        ActivityTypeHelper.ApplyTypeGroupFilter(query, typeGroup);

    // Shared filter pipeline for the activities grid — used by both the paged-rows and the
    // filtered-ids projections so they always agree. Caller applies _visibility first.
    private static IQueryable<Activity> ApplyActivityFilters(
        IQueryable<Activity> query,
        string? activityType,
        DateTime? startDate,
        DateTime? endDate,
        string? ownerUserId,
        IReadOnlyList<string>? activityTypes,
        GridFilterCollection? filters,
        IReadOnlyList<string>? ownerUserIds = null,
        bool ownerMatchNone = false,
        IReadOnlyCollection<TrackId>? restrictIds = null)
    {
        // Spatial pre-restriction: an explicit id allowlist (e.g. the tracks matching a map-drawn shape,
        // resolved by the caller). Kept geometry-free here — the caller intersects; this just AND-narrows.
        if (restrictIds is not null)
            query = query.Where(a => restrictIds.Contains(a.TrackId));

        // Multi-select exact types take precedence over the single group filter.
        if (activityTypes is { Count: > 0 })
            query = ActivityTypeHelper.ApplyTypesFilter(query, activityTypes);
        else if (!string.IsNullOrEmpty(activityType))
            query = ApplyTypeGroupFilter(query, activityType);

        if (startDate.HasValue)
            query = query.Where(a => a.StartDateTimeLocal >= startDate.Value);

        if (endDate.HasValue)
            query = query.Where(a => a.StartDateTimeLocal <= endDate.Value.AddDays(1));

        // Owner filter (identity space — resolved by OwnerFilterResolver). The scalar ownerUserId (external
        // deep-links) folds into the resolved set; an owner combo that matched nobody returns no rows.
        if (ownerMatchNone)
        {
            query = query.Where(a => false);
        }
        else
        {
            var ownerIds = new List<string>();
            if (!string.IsNullOrEmpty(ownerUserId)) ownerIds.Add(ownerUserId);
            if (ownerUserIds is { Count: > 0 }) ownerIds.AddRange(ownerUserIds);
            if (ownerIds.Count > 0)
                query = query.WhereInSet(a => a.UserId, ownerIds);
        }

        // Per-column filter-row predicates — dispatched generically by the column registry (the allowlist),
        // which also applies each column's display→storage unit scale.
        if (filters is { IsEmpty: false })
            query = ActivityGridColumns.Instance.ApplyFilters(query, filters.Items);

        return query;
    }
}
