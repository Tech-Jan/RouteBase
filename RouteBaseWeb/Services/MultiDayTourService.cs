using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace RouteBaseWeb.Services;

public class MultiDayTourService : IMultiDayTourService
{
    private readonly RouteBaseDbContext _context;
    private readonly IMemoryCache _cache;
    private readonly IVisibilityService _visibility;

    // Absolute TTL also bounds staleness from activity edits, which don't invalidate tour keys.
    private static readonly TimeSpan TourCacheTtl = TimeSpan.FromMinutes(10);

    // Cache key helper — delegates to CacheKeys to keep all key strings in one place.
    private string AllToursCacheKey => CacheKeys.AllMultiDayTours(_visibility.GetCurrentUserId());

    public MultiDayTourService(RouteBaseDbContext context, IMemoryCache cache, IVisibilityService visibility)
    {
        _context    = context;
        _cache      = cache;
        _visibility = visibility;
    }

    // Get all multi-day tours (read-only display). Tours are owner-only, so the actor-scoped
    // cache key is complete; entities are no-tracking since callers never mutate them.
    public async Task<List<MultiDayTour>> GetAllMultiDayToursAsync()
    {
        var cacheKey = AllToursCacheKey;
        if (_cache.TryGetValue(cacheKey, out List<MultiDayTour>? cached) && cached != null)
            return cached;

        var result = await _visibility.VisibleTours(_context.MultiDayTours)
            .AsNoTracking()
            .Include(m => m.Activities)
            .OrderByDescending(m => m.StartDate)
            .ToListAsync();

        _cache.Set(cacheKey, result, TourCacheTtl);
        return result;
    }

    /// <inheritdoc />
    public async Task<bool> DeleteTourAsync(int tourId)
    {
        // VisibleTours is owner-only for tours, so this doubles as the write gate (404 for foreign ids).
        var tour = await _visibility.VisibleTours(_context.MultiDayTours)
            .FirstOrDefaultAsync(t => t.MultiDayId == tourId);
        if (tour == null) return false;

        // B7 (user decision 2026-07-20): delete the tour row ONLY — activities keep living, they just
        // lose the tour link. The pointer is nulled explicitly first because legacy Activity has no
        // SET NULL FK on multi_day_ID (DatabaseResetService precedent); on fresh EF-created DBs the FK
        // does the same, so the explicit UPDATE is an idempotent no-op there. The Activity-table write
        // routes through the Sql seam (Services/Sql) like every other Activity writer.
        await using var tx = await _context.Database.BeginTransactionAsync();
        await _context.Database.ExecuteSqlRawAsync(Sql.MultiDayTourSql.UnlinkActivitiesSql, tourId);
        _context.MultiDayTours.Remove(tour);
        await _context.SaveChangesAsync();
        await tx.CommitAsync();

        _cache.Remove(AllToursCacheKey);
        return true;
    }
}
