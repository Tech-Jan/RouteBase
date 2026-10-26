using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Segments;
using RouteBaseWeb.Services.Tracks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace RouteBaseWeb.Services;

/// <summary>
/// Segment effort read-model: effort details and per-point performance data, plus effort counts and
/// single-effort removal. Effort summaries come from precomputed GPSinSegment columns (no GPS reads);
/// per-point chart data slices GPS windows out of decoded tracks. Cache reads/writes use the shared
/// <see cref="SegmentCache"/> options; per-point chainage math lives in <see cref="SegmentChainage"/>.
/// </summary>
public class SegmentEfforts : ISegmentEfforts
{
    private readonly RouteBaseDbContext _context;
    private readonly IMemoryCache _cache;
    private readonly ILogger<SegmentEfforts> _logger;
    private readonly IVisibilityService _visibility;
    private readonly SegmentCache _segCache;
    private readonly ITrackReader _trackReader;
    // The identity-free GPS→wire math, shared with the share path so the two cannot drift.
    private readonly ISegmentEffortPerformanceBuilder _perfBuilder;
    // Owner of the heatmap-row stale transition (B2): excluding/restoring an effort re-shapes painted spans.
    private readonly Heatmaps.IHeatmapLifecycle _heatmaps;

    private readonly TimeProvider _clock;

    public SegmentEfforts(
        RouteBaseDbContext context,
        IMemoryCache cache,
        ILogger<SegmentEfforts> logger,
        IVisibilityService visibility,
        SegmentCache segCache,
        ITrackReader trackReader,
        ISegmentEffortPerformanceBuilder perfBuilder,
        Heatmaps.IHeatmapLifecycle heatmaps,
        TimeProvider clock)
    {
        _clock = clock;
        _context     = context;
        _cache       = cache;
        _logger      = logger;
        _visibility  = visibility;
        _segCache    = segCache;
        _trackReader = trackReader;
        _perfBuilder = perfBuilder;
        _heatmaps    = heatmaps;
    }

    public async Task<List<SegmentEffortDetailed>> GetSegmentEffortsDetailedAsync(SegmentId segmentId, CancellationToken ct = default)
    {
        var userId = _visibility.GetCurrentUserId();
        // DELIBERATE EXCEPTION (A4, user decision 2026-07-20) to the "revocation bites before TTL" rule:
        // this per-viewer entry is served before any visibility gate, so a de-friended viewer keeps
        // reading their cached copy of this effort list for up to ShortTtl (5 min). Accepted tradeoff —
        // bounded staleness of aggregate rows the viewer was entitled to seconds ago, no per-point GPS.
        // The per-point sibling GetPerformanceDataForEffortsAsync implements the strict rule (re-gates on
        // every call, even full cache hits) because it serves GPS payloads. Audits: don't re-flag this.
        if (_cache.TryGetValue(CacheKeys.SegmentEfforts(userId, segmentId), out List<SegmentEffortDetailed>? cached) && cached != null)
            return cached;

        // SA-01: gate on the segment's own visibility. Cross-user matching means a non-visible
        // (private/stranger) segment can carry efforts on activities the caller CAN see; without
        // this gate, probing a segment id leaks existence/effort-count/GPS ids of an unseen segment.
        var segment = await _visibility.VisibleSegments(_context.Segments)
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.SegmentId == segmentId, ct);
        if (segment == null) return [];

        var rows = await _context.GpsInSegments
            .Where(g => g.SegmentId == segmentId)
            .AsNoTracking()
            .ToListAsync(ct);

        if (rows.Count == 0) return [];

        // Item 25: efforts-list read is a LEFT JOIN, not an anti-join — excluded efforts STAY in the
        // grid (marked, with a Restore action), unlike the totals/counts/chart reads which drop them.
        // Load this segment's exclusion identities once; per-row membership sets SegmentEffortDetailed.Excluded.
        var excluded = (await _context.SegmentEffortExclusions
                .Where(x => x.SegmentId == segmentId)
                .Select(x => new { x.TrackId, x.EntryIndex })
                .ToListAsync(ct))
            .Select(x => (x.TrackId, x.EntryIndex))
            .ToHashSet();

        // Diagnostic logging only at Debug to avoid string allocation overhead on every cold load.
        if (_logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug))
        {
            _logger.LogDebug("[Efforts] Segment {SegId}: {Count} rows in GPSinSegment", segmentId, rows.Count);
            foreach (var dbRow in rows)
            {
                _logger.LogDebug(
                    "[Efforts] Row #{Id} track={TrackId} guessed=[{EntryG},{ExitG}] calculated=[{EntryC},{ExitC}]",
                    dbRow.GpsInSegmentId, dbRow.TrackId,
                    dbRow.GpsIdEntryGuessed, dbRow.GpsIdExitGuessed,
                    dbRow.GpsIdEntryCalculated, dbRow.GpsIdExitCalculated);
            }
        }

        var trackIds = rows.Select(e => e.TrackId).Distinct().ToList();

        // Sequential on purpose: both queries share the scoped DbContext, and EF Core
        // forbids concurrent operations on one context (intermittent InvalidOperationException).
        var activityMap = await _visibility.VisibleActivities(_context.Activities)
            .Where(a => trackIds.Contains(a.TrackId))
            .Include(a => a.User)
            .AsNoTracking()
            .ToDictionaryAsync(a => a.TrackId, ct);

        // Resolve GPS ranges for every row upfront. The positional pattern narrows int? → int,
        // so no null-forgiving read is needed downstream (both-or-neither per ResolveGpsRange).
        var ranges = new List<(GpsInSegment row, int entry, int exit)>(rows.Count);
        foreach (var r in rows)
            if (r.ResolveGpsRange() is (int entry, int exit))
                ranges.Add((r, entry, exit));

        // Stored-columns only, no GPS fallback (same convention as the cards' totals): efforts
        // whose aggregates are still NULL (analysed before the avg columns existed) are left out
        // until their segment is next analysed.
        var result  = new List<SegmentEffortDetailed>(ranges.Count);
        int pending = 0;
        foreach (var (row, entry, exit) in ranges)
        {
            if (!activityMap.TryGetValue(row.TrackId, out var activity))
            {
                _logger.LogWarning("Activity not found for TrackId: {TrackId}", row.TrackId);
                continue;
            }
            if (SegmentEffortProjection.HasStoredAggregates(row))
            {
                bool isExcluded = excluded.Contains((row.TrackId, row.ExclusionEntryIndex));
                result.Add(SegmentEffortProjection.FromAggregates(
                    row, activity, segment, entry, exit, userId, isExcluded));
            }
            else
                pending++;
        }
        if (pending > 0)
            _logger.LogInformation(
                "Segment {SegId}: {Pending} effort(s) lack stored aggregates and are hidden — re-analyze the segment to fill them.",
                segmentId, pending);

        var ordered = result.OrderBy(e => e.ElapsedTime).ToList();
        // SA-07: tag with the segment owner's token so an owner mutation evicts this viewer's copy too.
        _cache.Set(CacheKeys.SegmentEfforts(userId, segmentId), ordered, _segCache.Options(SegmentCache.ShortTtl, segment.UserId));
        return ordered;
    }

    // See SegmentEffortProjection.NotExcluded — shared with the share path so a durable removal means
    // the same thing on both.
    private System.Linq.Expressions.Expression<Func<GpsInSegment, bool>> NotExcluded =>
        SegmentEffortProjection.NotExcluded(_context);

    public async Task<Dictionary<SegmentId, int>> GetEffortCountsForSegmentsAsync(List<SegmentId> segmentIds)
    {
        // SA-10: count only efforts on activities visible to the viewer (mirrors the totals query),
        // so the index card count can't exceed the totals/detailed list or leak an invisible-activity
        // effort's existence (e.g. after a friendship is revoked).
        //
        // KNOWN GAP (same class as GetPrecomputedEffortTotalsAsync's): the
        // SEGMENT itself is not re-gated here — only the per-effort activity visibility above. Safe while
        // callers pass ids from an already-visibility-gated list (Segments/Index does); raw ids would
        // reveal effort counts (existence + magnitude) on segments the caller cannot see.
        return await _context.GpsInSegments
            .Where(g => segmentIds.Contains(g.SegmentId))
            .Where(g => _visibility.VisibleActivities(_context.Activities).Any(a => a.TrackId == g.TrackId))
            .Where(NotExcluded) // item 25: excluded efforts don't count
            .GroupBy(g => g.SegmentId)
            .Select(grp => new { grp.Key, Count = grp.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
    }

    public async Task<Dictionary<SegmentId, SegmentEffortTotals>> GetEffortTotalsForSegmentsAsync(List<SegmentId> segmentIds, CancellationToken ct = default)
    {
        var result = new Dictionary<SegmentId, SegmentEffortTotals>();
        if (segmentIds.Count == 0) return result;

        var userId = _visibility.GetCurrentUserId();

        // Serve from cache where possible; only cold segments hit the precomputed-aggregate query below.
        // Reads precomputed columns only (no GPS / detailed-effort fallback) so the card matches the
        // Statistics page exactly — both show "-" until analysis fills the aggregates.
        // Cache-before-gate is the same accepted A4 de-friend window as GetSegmentEffortsDetailedAsync
        // (see the comment there) — totals are even coarser than the effort list. Audits: don't re-flag.
        var coldIds = new List<SegmentId>();
        foreach (var id in segmentIds.Distinct())
        {
            if (_cache.TryGetValue(CacheKeys.SegmentEffortTotals(userId, id), out SegmentEffortTotals? cached) && cached != null)
                result[id] = cached;
            else
                coldIds.Add(id);
        }
        if (coldIds.Count == 0) return result;

        // SA-07: tag each totals entry with its segment's owner token (one cheap projection) so an
        // owner mutation evicts other viewers' cached totals, not just the owner's own keys.
        var owners = await _context.Segments
            .Where(s => coldIds.Contains(s.SegmentId))
            .Select(s => new { s.SegmentId, s.UserId })
            .ToDictionaryAsync(x => x.SegmentId, x => x.UserId, ct);

        // Sum the precomputed per-effort aggregates (filled by analysis), scoped to visible
        // activities — a single grouped query, no GPS loading. Segments whose efforts predate
        // this precompute (any NULL aggregate) are left out of the result on purpose: the card
        // shows "-" until the next analyze populates them. There is no GPS fallback.
        var agg = await _context.GpsInSegments
            .Where(g => coldIds.Contains(g.SegmentId))
            .Where(g => _visibility.VisibleActivities(_context.Activities).Any(a => a.TrackId == g.TrackId))
            .Where(NotExcluded) // item 25: excluded efforts don't contribute to the card totals
            .GroupBy(g => g.SegmentId)
            .Select(grp => new
            {
                SegmentId = grp.Key,
                Count     = grp.Count(),
                Dist      = grp.Sum(x => x.EffortDistance  ?? 0),
                Elev      = grp.Sum(x => x.EffortElevation ?? 0),
                Time      = grp.Sum(x => x.EffortTime      ?? 0),
                Pending   = grp.Sum(x => x.EffortDistance == null ? 1 : 0),
            })
            .ToListAsync(ct);

        var aggById = agg.ToDictionary(a => a.SegmentId);
        foreach (var id in coldIds)
        {
            var opts = _segCache.Options(SegmentCache.ShortTtl, owners.GetValueOrDefault(id));

            // No visible efforts at all → cache empty so repeat loads skip it (card shows no total).
            if (!aggById.TryGetValue(id, out var a))
            {
                _cache.Set(CacheKeys.SegmentEffortTotals(userId, id), new SegmentEffortTotals(), opts);
                continue;
            }
            // Not-yet-analysed efforts present → don't cache, don't return; card shows "-".
            if (a.Pending > 0) continue;

            var totals = new SegmentEffortTotals
            {
                EffortCount    = a.Count,
                TotalDistance  = a.Dist,
                TotalElevation = a.Elev,
                TotalTime      = a.Time,
            };
            _cache.Set(CacheKeys.SegmentEffortTotals(userId, id), totals, opts);
            if (totals.EffortCount > 0) result[id] = totals;
        }
        return result;
    }

    /// <summary>
    /// Sums the precomputed per-effort aggregates for a single segment — the SAME source the segment
    /// list uses, so the Statistics page's "/ Total" matches the card exactly. Returns null when the
    /// segment has no visible efforts OR any aggregate is still NULL (pre-analyze); callers pair this
    /// with their own effort count to decide between "-" (pending) and nothing (no efforts). Never
    /// loads GPS and never falls back to GPS-derived detailed efforts.
    /// </summary>
    public async Task<SegmentEffortTotals?> GetPrecomputedEffortTotalsAsync(SegmentId segmentId, CancellationToken ct = default)
    {
        // Projection + the Pending ⇒ "-" rule live in SegmentEffortTotalsProjection, shared with the
        // share path so the two can't disagree about what the totals mean.
        return await SegmentEffortTotalsProjection.SumAsync(
            _context.GpsInSegments
                .Where(g => g.SegmentId == segmentId)
                .Where(g => _visibility.VisibleActivities(_context.Activities).Any(a => a.TrackId == g.TrackId))
                .Where(NotExcluded), // item 25: excluded efforts don't contribute to the Statistics "/ Total"
            ct);
    }

    public async Task<bool> RemoveEffortAsync(GpsInSegmentId effortId)
    {
        var effort = await _context.GpsInSegments.FindAsync(effortId);
        if (effort == null) return false;

        // Verify the parent segment is owned by the current user (I-8a: exclusion is owner-only).
        var segmentOwned = await _visibility.OwnedSegments(_context.Segments)
            .AnyAsync(s => s.SegmentId == effort.SegmentId);
        if (!segmentOwned) return false;

        // Item 25: removal is now durable. Instead of hard-deleting the GPSinSegment row (which
        // re-analysis would immediately re-create), insert a SegmentEffortExclusion the read consumers
        // anti-join out. Analysis stays untouched. Identity = (segment, track, entry=Calc??Guess).
        int entryIndex = effort.ExclusionEntryIndex;
        if (entryIndex < 0) return false; // no resolvable entry → can't form a stable identity

        var userId = _visibility.GetCurrentUserId();

        bool already = await _context.SegmentEffortExclusions.AnyAsync(x =>
            x.SegmentId == effort.SegmentId && x.TrackId == effort.TrackId && x.EntryIndex == entryIndex);
        if (!already)
        {
            _context.SegmentEffortExclusions.Add(new SegmentEffortExclusion
            {
                SegmentId  = effort.SegmentId,
                TrackId    = effort.TrackId,
                EntryIndex = entryIndex,
                ExcludedAt = _clock.GetUtcNow().UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            });
            await _context.SaveChangesAsync();
        }

        // SD-01: excluding an effort re-shapes the segment's painted spans, so flip attached done
        // heatmaps stale (still viewable, regenerated on demand) — heatmaps only repaint on regen.
        await _heatmaps.MarkStaleAsync(effort.SegmentId);

        // SE-04: evict AFTER commit so a concurrent read can't re-cache the excluded effort into a total.
        // SA-07: the invoker is the segment owner (OwnedSegments gate above), so tripping the owner token
        // via InvalidateUser evicts every viewer's segment-derived entries, not just the invoker's keys.
        _segCache.InvalidateSegment(userId, effort.SegmentId);
        _segCache.InvalidateUser(userId);

        _logger.LogInformation("Excluded effort {EffortId} (segment {SegmentId}, track {TrackId}, entry {Entry})",
            effortId, effort.SegmentId, effort.TrackId, entryIndex);
        return true;
    }

    public async Task<bool> RestoreEffortAsync(GpsInSegmentId effortId)
    {
        var effort = await _context.GpsInSegments.FindAsync(effortId);
        if (effort == null) return false;

        // I-8a: restore is owner-only, same gate as removal.
        var segmentOwned = await _visibility.OwnedSegments(_context.Segments)
            .AnyAsync(s => s.SegmentId == effort.SegmentId);
        if (!segmentOwned) return false;

        int entryIndex = effort.ExclusionEntryIndex;
        var userId = _visibility.GetCurrentUserId();

        // Item 25: restore = delete the exclusion row. Idempotent (no-op when not excluded).
        await _context.SegmentEffortExclusions
            .Where(x => x.SegmentId == effort.SegmentId && x.TrackId == effort.TrackId && x.EntryIndex == entryIndex)
            .ExecuteDeleteAsync();

        await _heatmaps.MarkStaleAsync(effort.SegmentId);

        _segCache.InvalidateSegment(userId, effort.SegmentId);
        _segCache.InvalidateUser(userId);

        _logger.LogInformation("Restored effort {EffortId} (segment {SegmentId}, track {TrackId}, entry {Entry})",
            effortId, effort.SegmentId, effort.TrackId, entryIndex);
        return true;
    }

    /// <summary>
    /// Performance data for a specific set of efforts — the chunked chart loader's per-batch call.
    /// Loads GPS for only the requested efforts and caches each effort separately, so the server's
    /// working set stays bounded to one chunk regardless of how many efforts the segment has.
    /// When <paramref name="maxPointsPerEffort"/> &gt; 0 each effort is stride-downsampled to that
    /// budget after the full-slice build (endpoints pinned); 0 keeps full resolution.
    /// </summary>
    public async Task<List<EffortPerformanceData>> GetPerformanceDataForEffortsAsync(
        SegmentId segmentId, IReadOnlySet<GpsInSegmentId> effortIds, int maxPointsPerEffort = 0, CancellationToken ct = default)
    {
        if (effortIds.Count == 0) return [];
        var userId = _visibility.GetCurrentUserId();

        // SA-02: gate on the segment's own visibility before serving per-point chart data (same class
        // as SA-01, larger payload). Runs even on full-cache-hit requests so revocation bites before TTL.
        // SA-07: also capture the owner id to tag cache entries with the owner's change token.
        var owner = await _visibility.VisibleSegments(_context.Segments)
            .Where(s => s.SegmentId == segmentId)
            .Select(s => new { s.UserId })
            .FirstOrDefaultAsync(ct);
        if (owner == null) return [];
        string? ownerUserId = owner.UserId;

        // Serve cached per-effort entries; only cold efforts hit the DB below. The budget is part of
        // the key, so full-res and each downsampled variant cache independently.
        var result  = new List<EffortPerformanceData>(effortIds.Count);
        var coldIds = new List<GpsInSegmentId>();
        foreach (var eid in effortIds)
        {
            if (_cache.TryGetValue(CacheKeys.SegmentPerformanceEffort(userId, segmentId, eid, maxPointsPerEffort),
                    out EffortPerformanceData? hit) && hit != null)
                result.Add(hit);
            else
                coldIds.Add(eid);
        }
        if (coldIds.Count == 0) return result;

        // Load only the requested efforts' rows + their owning activities. Item 25: excluded efforts get
        // no chart data — the effort-cost / comparison curves anti-join them out even if the client asks.
        var rows = await _context.GpsInSegments
            .Where(g => g.SegmentId == segmentId && coldIds.Contains(g.GpsInSegmentId))
            .Where(NotExcluded)
            .AsNoTracking()
            .ToListAsync(ct);
        if (rows.Count == 0) return result;

        var ranges = rows
            .Select(r => (row: r, range: r.ResolveGpsRange()))
            .Where(x => x.range.entry.HasValue)
            .ToList();

        var trackIds = ranges.Select(x => x.row.TrackId).Distinct().ToList();
        var activityMap = await _visibility.VisibleActivities(_context.Activities)
            .Where(a => trackIds.Contains(a.TrackId))
            .Include(a => a.User)
            .AsNoTracking()
            .ToDictionaryAsync(a => a.TrackId, ct);

        // The gate ends here: pair each ranged row with its VISIBLE activity. Everything past this point
        // is identity-free math, shared with the share path via ISegmentEffortPerformanceBuilder.
        var authorized = new List<(GpsInSegment Row, Models.Activity Activity)>(ranges.Count);
        foreach (var x in ranges)
        {
            if (activityMap.TryGetValue(x.row.TrackId, out var activity)) authorized.Add((x.row, activity));
            else _logger.LogWarning("Activity not found for TrackId: {TrackId}", x.row.TrackId);
        }

        var built = await _perfBuilder.BuildAsync(segmentId, authorized, maxPointsPerEffort, ct);

        // Cached HERE, not in the builder: the key family is what keeps each caller's entries in its own
        // namespace. SA-07: tagged with the segment owner's token so an owner mutation evicts this copy.
        var opts = _segCache.Options(SegmentCache.ShortTtl, ownerUserId);
        foreach (var epd in built)
        {
            _cache.Set(CacheKeys.SegmentPerformanceEffort(userId, segmentId, new GpsInSegmentId(epd.EffortId), maxPointsPerEffort), epd, opts);
            result.Add(epd);
        }
        return result;
    }

    public async Task<EffortsSummaryDto> GetEffortPolylinesAsync(
        SegmentId segmentId, int maxEfforts, int maxPointsPerEffort, CancellationToken ct = default)
    {
        var userId = _visibility.GetCurrentUserId();
        var cacheKey = CacheKeys.SegmentEffortPolylines(userId, segmentId, maxEfforts, maxPointsPerEffort);
        if (_cache.TryGetValue(cacheKey, out EffortsSummaryDto? cached) && cached != null)
            return cached;

        // Same visibility gate as the other effort reads (SA-01/02): a non-visible segment must not leak
        // its efforts; also capture the owner id to tag the cache entry with the owner's change token.
        var owner = await _visibility.VisibleSegments(_context.Segments)
            .Where(s => s.SegmentId == segmentId)
            .Select(s => new { s.UserId })
            .FirstOrDefaultAsync(ct);
        if (owner == null) return new EffortsSummaryDto([], false);

        // Excluded efforts don't count — the preview traces only what the totals do (item 25).
        // Ordered by PK so the maxEfforts cut below is deterministic — an unordered read let the kept
        // subset change between cache misses.
        var rows = await _context.GpsInSegments
            .Where(g => g.SegmentId == segmentId)
            .Where(NotExcluded)
            .OrderBy(g => g.GpsInSegmentId)
            .AsNoTracking()
            .ToListAsync(ct);

        var ranges = new List<(GpsInSegment row, int entry, int exit)>(rows.Count);
        foreach (var r in rows)
            if (r.ResolveGpsRange() is (int entry, int exit))
                ranges.Add((r, entry, exit));

        // Visibility BEFORE the cap: the cap must count what the viewer can SEE. Cutting first spent the
        // quota on invisible (foreign) efforts that were then filtered away — fewer than maxEfforts
        // polylines with Truncated=true even when enough visible efforts existed.
        var candidateTrackIds = ranges.Select(x => x.row.TrackId).Distinct().ToList();
        var visibleTracks = (await _visibility.VisibleActivities(_context.Activities)
                .Where(a => candidateTrackIds.Contains(a.TrackId))
                .Select(a => a.TrackId)
                .ToListAsync(ct))
            .ToHashSet();
        ranges = ranges.Where(x => visibleTracks.Contains(x.row.TrackId)).ToList();

        // Cap the effort count BEFORE any decode so a popular segment can't force hundreds of slices on
        // one thumbnail load. The cut is flagged (Truncated), never silent.
        bool truncated = ranges.Count > maxEfforts;
        if (truncated) ranges = ranges.GetRange(0, maxEfforts);

        var efforts = new List<EffortPolylineDto>(ranges.Count);
        foreach (var group in ranges.GroupBy(x => x.row.TrackId))
        {
            var track = await DecodeTrackAsync(group.Key, ct);   // one decode per track, LRU-shared
            if (track == null) continue;

            foreach (var (_, entry, exit) in group)
            {
                var (lo, hi) = track.OrdinalRange(entry, exit);
                if (hi < lo) continue;

                // A polyline needs only lat/lng at the sampled ordinals — read the two channels
                // directly instead of materializing a full GpsSlice (9 channels + DateTime) per point.
                var points = new List<LatLngDto>();
                foreach (var i in EffortPointSampler.SampleIndices(hi - lo + 1, maxPointsPerEffort))
                {
                    double la = track.Lat[lo + i], ln = track.Lng[lo + i];
                    if (!double.IsNaN(la) && !double.IsNaN(ln))
                        points.Add(new LatLngDto(GpsPrecision.Coord(la), GpsPrecision.Coord(ln)));
                }
                if (points.Count >= 2) efforts.Add(new EffortPolylineDto(points));
            }
        }

        var result = new EffortsSummaryDto(efforts, truncated);
        _cache.Set(cacheKey, result, _segCache.Options(SegmentCache.ShortTtl, owner.UserId));
        return result;
    }

    // -------------------------------------------------------------------------
    // Private helpers
    // -------------------------------------------------------------------------

    /// <summary>Decodes one track via the injected reader (LRU-cached, shared with every consumer).</summary>
    private Task<DecodedTrack?> DecodeTrackAsync(TrackId trackId, CancellationToken ct) =>
        _trackReader.GetTrackAsync(trackId, ct);

}
