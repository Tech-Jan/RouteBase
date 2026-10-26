using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace RouteBaseWeb.Services;

/// <summary>
/// Segment CRUD: listing, paging, creating (polyline + shape), in-place shape edits, deleting, and GPS
/// summaries. Delete/update route cache eviction through the shared <see cref="SegmentCache"/> so the
/// Efforts read-model's keys are cleared too. Geometry math lives in <see cref="SegmentGeometry"/>.
/// Heatmap row + tile ownership lives in <see cref="Heatmaps.IHeatmapLifecycle"/> (B2); delete calls it
/// at the tile-purge seam.
/// </summary>
public class SegmentCrud : ISegmentCrud
{
    private readonly RouteBaseDbContext _context;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SegmentCrud> _logger;
    private readonly IVisibilityService _visibility;
    private readonly SegmentCache _segCache;
    private readonly Heatmaps.IHeatmapLifecycle _heatmaps;
    private readonly IProfileSettingsService _settings;

    private readonly TimeProvider _clock;

    public SegmentCrud(
        RouteBaseDbContext context,
        IMemoryCache cache,
        ILogger<SegmentCrud> logger,
        IVisibilityService visibility,
        SegmentCache segCache,
        Heatmaps.IHeatmapLifecycle heatmaps,
        IProfileSettingsService settings,
        TimeProvider clock)
    {
        _clock = clock;
        _context    = context;
        _cache      = cache;
        _logger     = logger;
        _visibility = visibility;
        _segCache   = segCache;
        _heatmaps   = heatmaps;
        _settings   = settings;
    }

    /// <summary>
    /// The creating user's default visibility for new segments.
    ///
    /// SettingKeys.DefaultVisibility describes itself as covering "new activities and segments" but was
    /// read by NOTHING before this: every segment ever created sat at the CLR default "friends", and the
    /// setting was dead. Segments now honour it. (Activities still do not — they take visibility from the
    /// recorder request or an explicit edit; wiring that is a separate change with its own blast radius.)
    /// </summary>
    private async Task<string> DefaultVisibilityAsync(string? userId)
    {
        var configured = await _settings.GetSettingAsync<string>(
            SettingCategories.Privacy, SettingKeys.DefaultVisibility, userId, Visibilities.Default);
        // An unrecognised stored value falls back rather than persisting something VisibilityService
        // would not match — which would make the segment invisible to everyone including its owner.
        return Visibilities.Normalize(configured) ?? Visibilities.Default;
    }

    /// <summary>
    /// Sets a segment's visibility. Owner-only via OwnedSegments, mirroring
    /// ActivityCommandService.UpdateActivityAsync — including the cache evict, so the change is reflected
    /// immediately instead of after the sliding TTL.
    /// </summary>
    public async Task<bool> UpdateSegmentVisibilityAsync(SegmentId segmentId, string visibility)
    {
        var normalized = Visibilities.Normalize(visibility);
        if (normalized == null) return false;

        var segment = await _visibility.OwnedSegments(_context.Segments)
            .FirstOrDefaultAsync(s => s.SegmentId == segmentId);
        if (segment == null) return false;   // not yours or not there — the same answer, deliberately

        var previous = segment.Visibility;
        segment.Visibility = normalized;
        await _context.SaveChangesAsync();

        // SEC-01: heatmap tiles are painted for the segment's audience. Wider → the old tiles may show the new
        // viewers rides they can't see, so they go offline until regenerated. Narrower → still safe to serve,
        // but the owner could now see more, so offer a regenerate.
        if (Heatmaps.HeatmapPaintScope.Widens(previous, normalized))
            await _heatmaps.WithdrawAsync(segmentId);
        else if (Heatmaps.HeatmapPaintScope.Widens(normalized, previous))
            await _heatmaps.MarkStaleAsync(segmentId);

        // Visibility drives who VisibleSegments matches, so every viewer's cached derived data for this
        // segment is now potentially wrong. Same shape as the activity path.
        var ownerId = segment.UserId;
        if (ownerId != null)
        {
            _segCache.InvalidateSegment(ownerId, segmentId);
            _segCache.InvalidateUser(ownerId);
        }

        _logger.LogInformation("Segment {SegmentId} visibility set to {Visibility}", segmentId, normalized);
        return true;
    }

    public async Task<List<Segment>> GetAllSegmentsAsync()
    {
        // Render-only consumers (API list DTO, Segments index cards) — never mutated.
        // SegmentId is a deterministic tiebreak so same-named segments have a defined order — the cards
        // endpoint's "head of the full list" invariant (GetTopSegmentCardsAsync) rests on it (SVC-02).
        return await _visibility.VisibleSegments(_context.Segments)
            .AsNoTracking()
            .OrderBy(s => s.SegmentName)
            .ThenBy(s => s.SegmentId)
            .ToListAsync();
    }

    public async Task<List<SegmentCardDto>> GetTopSegmentCardsAsync(int limit)
    {
        // Projected + limited in SQL: the dashboard widget renders three rows and used to pull every
        // visible segment across the wire to do it (PERF-4). Same gate and ordering (incl. the SegmentId
        // tiebreak) as the full list, so the card set is always the head of what /Segments shows.
        return await _visibility.VisibleSegments(_context.Segments)
            .AsNoTracking()
            .OrderBy(s => s.SegmentName)
            .ThenBy(s => s.SegmentId)
            .Take(limit)
            .Select(s => new SegmentCardDto(s.SegmentId.Value, s.SegmentName, s.SegmentDistance, s.SegmentAscent))
            .ToListAsync();
    }

    public async Task<bool> SegmentExistsAsync(SegmentId segmentId)
    {
        return await _visibility.VisibleSegments(_context.Segments)
            .AnyAsync(s => s.SegmentId == segmentId);
    }

    public async Task<List<Segment>> GetSegmentsForActivityAsync(TrackId activityId)
    {
        // Gate on activity visibility first: without this, a probe of arbitrary ids reveals which
        // visible segments a stranger's private activity crossed (existence + geo correlation).
        var activityVisible = await _visibility.VisibleActivities(_context.Activities)
            .AnyAsync(a => a.TrackId == activityId);
        if (!activityVisible) return new List<Segment>();

        // Use an explicit join so EF Core emits a proper JOIN + GROUP BY / DISTINCT
        // rather than loading GpsInSegment rows and navigating to Segment in memory
        // (which returns nulls when the navigation property is not Included).
        var segmentIds = await _context.GpsInSegments
            .Where(gis => gis.TrackId == activityId)
            .Select(gis => gis.SegmentId)
            .Distinct()
            .ToListAsync();

        if (segmentIds.Count == 0)
            return new List<Segment>();

        return await _visibility.VisibleSegments(_context.Segments)
            .AsNoTracking()
            .Where(s => segmentIds.Contains(s.SegmentId))
            .OrderBy(s => s.SegmentName)
            .ToListAsync();
    }

    public async Task<Segment?> GetSegmentWithGpsAsync(SegmentId segmentId)
    {
        // Render-only consumers (API GET, Statistics page) — never mutated, so no change tracking.
        return await _visibility.VisibleSegments(_context.Segments)
            .Include(s => s.GpsData.OrderBy(g => g.SegGpsId))
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.SegmentId == segmentId);
    }

    public async Task<SegmentRuleFields?> GetSegmentRulesAsync(SegmentId segmentId)
    {
        return await _visibility.VisibleSegments(_context.Segments)
            .Where(s => s.SegmentId == segmentId)
            .Select(s => new SegmentRuleFields(s.SegmentKind, s.MatchRulesJson))
            .FirstOrDefaultAsync();
    }

    // SA-03: owner-scoped, no GPS Include — used by the edit-payload endpoint so a friend/public viewer
    // can't read the raw rule document + owner friend-id list of a segment they can only view.
    public async Task<Segment?> GetOwnedSegmentAsync(SegmentId segmentId)
    {
        return await _visibility.OwnedSegments(_context.Segments)
            .FirstOrDefaultAsync(s => s.SegmentId == segmentId);
    }

    /// <summary>
    /// Returns up to <paramref name="maxPoints"/> downsampled lat/lng pairs for a segment
    /// without loading the full <see cref="SegmentGps"/> entity graph.
    /// Used by the minimap summary endpoint.
    /// </summary>
    public async Task<List<GpsCoordinate>> GetSegmentGpsSummaryAsync(SegmentId segmentId, int maxPoints = 50)
    {
        var userId = _visibility.GetCurrentUserId();
        var cacheKey = CacheKeys.SegmentGpsSummary(userId, segmentId, maxPoints);
        if (_cache.TryGetValue(cacheKey, out List<GpsCoordinate>? cached) && cached != null)
            return cached;

        // Verify the segment is visible before querying its GPS; capture the owner id (SA-07) so the
        // cached summary carries the owner's change token and evicts on the owner's delete/id-reuse.
        var owner = await _visibility.VisibleSegments(_context.Segments)
            .Where(s => s.SegmentId == segmentId)
            .Select(s => new { s.UserId })
            .FirstOrDefaultAsync();
        if (owner == null)
            return new List<GpsCoordinate>();

        var allPoints = await _context.SegmentGps
            .Where(g => g.SegmentId == segmentId && g.GpsLat != null && g.GpsLong != null)
            .OrderBy(g => g.SegGpsId)
            .Select(g => new GpsCoordinate { Lat = g.GpsLat!.Value, Lng = g.GpsLong!.Value })
            .ToListAsync();

        if (allPoints.Count == 0)
            return allPoints;

        List<GpsCoordinate> result;
        if (allPoints.Count <= maxPoints)
        {
            result = allPoints;
        }
        else
        {
            // SA-11: ceil so the stride never returns up to 2×maxPoints−1 (floor let 99 pts / max 50
            // return all 99 — the endpoint doc promises "up to maxPoints").
            var factor = Math.Max(1, (allPoints.Count + maxPoints - 1) / maxPoints);
            result = allPoints
                .Where((_, i) => i % factor == 0)
                .ToList();
        }

        _cache.Set(cacheKey, result, _segCache.Options(SegmentCache.LongTtl, owner.UserId));
        return result;
    }

    public async Task<Segment> CreateSegmentAsync(string segmentName, string activityType, List<SegmentGpsPoint> gpsPoints)
    {
        // Distance + elevation are computed on the RAW points (cheap O(n)) so a whole-activity segment
        // keeps full accuracy regardless of how much the working set below is thinned.
        var rawDistance = SegmentGeometry.CalculateDistance(gpsPoints);
        var (rawAscent, rawDescent) = SegmentGeometry.CalculateElevation(gpsPoints);

        // Bound the input handed to RDP so its recursion depth / O(n²) scan can't be driven by raw
        // length — this is what makes any-length activities safe to accept (see RdpWorkingCap).
        var working = SegmentGeometry.StrideDownsample(gpsPoints, SegmentGeometry.RdpWorkingCap);

        // Adaptive Ramer-Douglas-Peucker: ~1 point per 40 m, floored at 50 and capped at 2000 so a long
        // whole-activity segment keeps real shape instead of being crushed to 200 points. Upper-bounded by
        // RdpWorkingCap (the stride cap that feeds RDP) and by per-stored-point matching cost.
        int targetPoints = Math.Max(50, Math.Min(2000, (int)(rawDistance / 40)));
        double tolerance = 0.00005;
        var simplified = SegmentGeometry.SimplifySegmentPoints(working, tolerance);

        while (simplified.Count > targetPoints && tolerance < 0.0002)
        {
            tolerance *= 1.5;
            simplified = SegmentGeometry.SimplifySegmentPoints(working, tolerance);
        }
        while (simplified.Count < Math.Min(50, targetPoints) && tolerance > 0.00001)
        {
            tolerance *= 0.7;
            simplified = SegmentGeometry.SimplifySegmentPoints(working, tolerance);
        }

        _logger.LogInformation(
            "Segment: {Distance:F2}km, {Original}→{Simplified} pts (target={Target}, tol={Tolerance:F6})",
            rawDistance / 1000, gpsPoints.Count, simplified.Count, targetPoints, tolerance);

        var creatorId = _visibility.GetCurrentUserId();
        var segment = new Segment
        {
            SegmentName     = segmentName,
            ActivityType    = activityType,
            SegmentDistance = rawDistance,
            // double since J2 — the int cast truncated real ascent to whole metres at write time.
            SegmentAscent   = rawAscent,
            SegmentDescent  = rawDescent,
            CreatedAt       = _clock.GetUtcNow().UtcDateTime,
            UserId          = creatorId,
            Visibility      = await DefaultVisibilityAsync(creatorId)
        };

        _context.Segments.Add(segment);
        await _context.SaveChangesAsync();

        foreach (var pt in simplified)
        {
            _context.SegmentGps.Add(new SegmentGps
            {
                SegmentId = segment.SegmentId,
                GpsLat    = pt.Lat,
                GpsLong   = pt.Lng,
                Altitude  = pt.Altitude
            });
        }

        await _context.SaveChangesAsync();
        return segment;
    }

    public async Task<Segment> CreateShapeSegmentAsync(string segmentName, string activityType, string rulesJson)
    {
        // Shape segments carry no polyline geometry. Distance/ascent stay null for now —
        // they are cosmetic for this kind and not required by the stats/replay page.
        // rulesJson is the canonical serialization of an already-validated RuleDocument;
        // owner scope now lives inside it (an owner track filter), so match_owner_ids is no longer written.
        var creatorId = _visibility.GetCurrentUserId();
        var segment = new Segment
        {
            SegmentName    = segmentName,
            ActivityType   = activityType,
            SegmentKind    = SegmentKinds.Shapes,
            MatchRulesJson = rulesJson,
            CreatedAt      = _clock.GetUtcNow().UtcDateTime,
            UserId         = creatorId,
            Visibility     = await DefaultVisibilityAsync(creatorId)
        };

        _context.Segments.Add(segment);
        await _context.SaveChangesAsync();
        _logger.LogInformation("Created shape segment {SegmentId}", segment.SegmentId);
        return segment;
    }

    public async Task<Segment?> UpdateShapeSegmentAsync(SegmentId segmentId, string segmentName, string activityType, string rulesJson)
    {
        var segment = await _visibility.OwnedSegments(_context.Segments)
            .FirstOrDefaultAsync(s => s.SegmentId == segmentId);
        // Only the owner may edit, and only shape segments carry a rule document.
        if (segment == null || segment.SegmentKind != SegmentKinds.Shapes)
            return null;

        // Replace the document wholesale (owner scope now lives inside it). Efforts (GPSinSegment) are
        // left alone here — the caller re-queues single-segment analysis, which deletes prior efforts
        // before re-matching against the new rules. match_owner_ids is left untouched (read-only fallback).
        segment.SegmentName    = segmentName;
        segment.ActivityType   = activityType;
        segment.MatchRulesJson = rulesJson;

        await _context.SaveChangesAsync();

        var userId = _visibility.GetCurrentUserId();
        _segCache.InvalidateSegment(userId, segmentId);
        _segCache.InvalidateUser(userId);

        _logger.LogInformation("Updated shape segment {SegmentId}", segmentId);
        return segment;
    }

    public async Task<bool> DeleteSegmentAsync(SegmentId segmentId)
    {
        var segment = await _visibility.OwnedSegments(_context.Segments)
            .FirstOrDefaultAsync(s => s.SegmentId == segmentId);
        if (segment == null) return false;

        var userId = _visibility.GetCurrentUserId();

        // Capture the heatmap id before the cascade removes its row — its tile file lives on the
        // filesystem now, so the DB cascade no longer frees it (purged best-effort after commit via
        // the lifecycle owner, which holds the pack-store handle — B2).
        var heatmapId = await _heatmaps.CaptureIdForSegmentAsync(segmentId);

        // SA-06: the child deletes + segment removal must be atomic — one explicit transaction;
        // SaveChangesAsync and the raw deletes join the ambient tx on the same connection. Both pinned
        // schema vintages DO cascade GPSinSegment/Segment_GPS/Share_Link (legacy-baseline.sql + fresh EF),
        // so those explicit deletes are belt-and-braces; SegmentEffortExclusion is the one genuinely
        // FK-less child and NEEDS its delete. The child-table list (and each table's why) is owned by
        // ChildTableRegistry (CR5-05); Heatmap rows cascade via FK, tile file purged below.
        await using (var tx = await _context.Database.BeginTransactionAsync())
        {
            foreach (var child in ChildTableRegistry.SegmentChildTables)
                await _context.Database.ExecuteSqlRawAsync(child.DeleteByKeySql, segmentId.Value);
            _context.Segments.Remove(segment);
            await _context.SaveChangesAsync();
            await tx.CommitAsync();
        }

        // SA-08: evict AFTER commit so a concurrent read can't re-cache the doomed data past the delete.
        _segCache.InvalidateSegment(userId, segmentId);
        // InvalidateSegment can't target SegmentGpsSummary (keyed per maxPoints), so trip the whole
        // user token to clear the deleted segment's GPS summary too.
        _segCache.InvalidateUser(userId);

        if (heatmapId is int id)
            _heatmaps.PurgeTiles(id);

        _logger.LogInformation("Deleted segment {SegmentId}", segmentId);
        return true;
    }
}
