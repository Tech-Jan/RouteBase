using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace RouteBaseWeb.Services.Heatmaps;

/// <summary>
/// In-module owner of the <see cref="Heatmap"/> row's non-generation writes and the tile-file handle
/// (B2). Bodies moved verbatim from <c>SegmentCrud</c>'s former heatmap methods; the three duplicate
/// mark-stale implementations (SegmentAnalysis / SegmentEfforts / DatabaseResetService) collapse into
/// the single <see cref="MarkStaleAsync"/> here (the reset path keeps its own bulk SQL by ruling).
/// </summary>
public sealed class HeatmapLifecycle : IHeatmapLifecycle
{
    private readonly RouteBaseDbContext _context;
    private readonly IVisibilityService _visibility;
    private readonly HeatmapTilePackStore _tiles;
    private readonly ILogger<HeatmapLifecycle> _logger;

    public HeatmapLifecycle(
        RouteBaseDbContext context,
        IVisibilityService visibility,
        HeatmapTilePackStore tiles,
        ILogger<HeatmapLifecycle> logger)
    {
        _context    = context;
        _visibility = visibility;
        _tiles      = tiles;
        _logger     = logger;
    }

    public async Task<HeatmapAttachResult> AttachAsync(SegmentId segmentId, IEnumerable<string> activityTypes)
    {
        // A heatmap is an optional attachment on a normal shapes segment; efforts already carry the
        // MatchMode-correct spans, so generation reads GpsInSegments — no shape re-matching here.
        var owned = await _visibility.OwnedSegments(_context.Segments)
            .AnyAsync(s => s.SegmentId == segmentId && s.SegmentKind == SegmentKinds.Shapes);
        if (!owned) return HeatmapAttachResult.NotFound;

        var heatmap = await _context.Heatmaps.FirstOrDefaultAsync(h => h.SegmentId == segmentId);
        // Don't disturb an in-flight render; the caller retries after it settles.
        if (heatmap is { Status: HeatmapStatuses.Pending or HeatmapStatuses.Running }) return HeatmapAttachResult.Conflict;

        // HM-15: the list round-trips through a comma-joined column, so an element containing ','
        // would split into different tokens at generation time (filter silently mismatches). Real
        // ActivityType values are short and comma-free (sanitized at import); cap count + length.
        var types = activityTypes?
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Where(t => t.Length <= 50 && !t.Contains(',', StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .Take(20)
            .ToList() ?? [];
        var typeCsv = types.Count > 0 ? string.Join(",", types) : null;

        if (heatmap == null)
        {
            _context.Heatmaps.Add(new Heatmap { SegmentId = segmentId, Status = HeatmapStatuses.Pending, ActivityTypes = typeCsv });
        }
        else
        {
            // Regeneration: reset to pending and clear prior render metadata (tiles are deleted at job start).
            heatmap.Status        = HeatmapStatuses.Pending;
            heatmap.Error         = null;
            heatmap.MinZoom       = null;
            heatmap.MaxZoom       = null;
            heatmap.CountMax      = null;
            heatmap.BoundsSouth   = null;
            heatmap.BoundsWest    = null;
            heatmap.BoundsNorth   = null;
            heatmap.BoundsEast    = null;
            heatmap.ActivityIds   = null;
            heatmap.GeneratedAt   = null;
            heatmap.ActivityTypes = typeCsv;
        }

        await _context.SaveChangesAsync();
        _logger.LogInformation("Attached/reset heatmap on segment {SegmentId} (types: {Types})", segmentId, typeCsv ?? "all");
        return HeatmapAttachResult.Attached;
    }

    public async Task FailPendingAsync(SegmentId segmentId, string error)
    {
        var heatmap = await _context.Heatmaps.FirstOrDefaultAsync(h => h.SegmentId == segmentId);
        // Only demote a pending row — never clobber a run the worker already started (running/done/failed).
        if (heatmap is not { Status: HeatmapStatuses.Pending }) return;
        heatmap.Status = HeatmapStatuses.Failed;
        heatmap.Error  = error;
        await _context.SaveChangesAsync();
    }

    public async Task<Heatmap?> GetVisibleAsync(SegmentId segmentId, CancellationToken ct = default)
    {
        // Visibility-scoped: only surfaces the heatmap when its owning segment is visible to the caller.
        var visible = await _visibility.VisibleSegments(_context.Segments)
            .AnyAsync(s => s.SegmentId == segmentId, ct);
        if (!visible) return null;

        return await _context.Heatmaps.AsNoTracking()
            .FirstOrDefaultAsync(h => h.SegmentId == segmentId, ct);
    }

    // SD-01: mark every attached, currently-`done` heatmap `stale` — its tiles were painted from the old
    // GPSinSegment spans a re-analysis / exclusion / restore just re-shaped. Idempotent (WHERE status='done'
    // → repeat calls no-op); tiles stay viewable, regeneration is opt-in (no queue thrash). One indexed UPDATE.
    public async Task MarkStaleAsync(SegmentId segmentId, CancellationToken ct = default) =>
        await _context.Heatmaps
            .Where(h => h.SegmentId == segmentId && h.Status == HeatmapStatuses.Done)
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, HeatmapStatuses.Stale), ct);

    public async Task WithdrawAsync(SegmentId segmentId, CancellationToken ct = default) =>
        await _context.Heatmaps
            .Where(h => h.SegmentId == segmentId
                     && (h.Status == HeatmapStatuses.Done || h.Status == HeatmapStatuses.Stale))
            .ExecuteUpdateAsync(s => s
                .SetProperty(h => h.Status, HeatmapStatuses.Failed)
                .SetProperty(h => h.Error, HeatmapPaintScope.WidenedMessage), ct);

    // Same transition as MarkStaleAsync, addressed by TRACK: the ride left (or entered) circulation, so every
    // heatmap it has efforts on was painted from spans that no longer hold. One indexed UPDATE with the segment
    // set as a subquery — the caller used to fetch the ids and then issue an UPDATE per segment.
    public async Task MarkStaleForTrackAsync(TrackId trackId, CancellationToken ct = default) =>
        await _context.Heatmaps
            .Where(h => h.Status == HeatmapStatuses.Done
                     && _context.GpsInSegments.Any(g => g.TrackId == trackId && g.SegmentId == h.SegmentId))
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, HeatmapStatuses.Stale), ct);

    // Batch twin for the lifecycle batch seam (P7): still ONE indexed UPDATE — the track set just
    // widens the effort subquery.
    public async Task MarkStaleForTracksAsync(IReadOnlyCollection<TrackId> trackIds, CancellationToken ct = default) =>
        await _context.Heatmaps
            .Where(h => h.Status == HeatmapStatuses.Done
                     && _context.GpsInSegments.Any(g => trackIds.Contains(g.TrackId) && g.SegmentId == h.SegmentId))
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.Status, HeatmapStatuses.Stale), ct);

    public async Task<int?> CaptureIdForSegmentAsync(SegmentId segmentId, CancellationToken ct = default) =>
        await _context.Heatmaps
            .Where(h => h.SegmentId == segmentId)
            .Select(h => (int?)h.HeatmapId)
            .FirstOrDefaultAsync(ct);

    public void PurgeTiles(int heatmapId) => _tiles.Delete(heatmapId);
}
