using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Tracks;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.Heatmaps;

/// <summary>One effort's GPS window to paint: a track and its (optional) entry→exit span.</summary>
internal readonly record struct EffortSpan(int TrackId, int? Entry, int? Exit);

/// <summary>
/// Job entry for heatmap generation: read the segment's efforts (GpsInSegments, one span per effort,
/// optionally filtered by activity type) → paint each span's GPS window at z_max → build the pyramid,
/// flushing tiles to the DB per zoom → write status + metadata. The efforts already encode MatchMode
/// (whole vs clip) in their stored spans, so no shape re-match happens here. Owns the Heatmap row's DB
/// status; the in-memory <see cref="HeatmapProgress"/> lifecycle (start/complete/fail) is the worker's.
/// </summary>
public sealed class HeatmapGenerationService(
    RouteBaseDbContext context,
    HeatmapTilePackStore tileStore,
    HeatmapPyramidBuilder builder,
    HeatmapProgress progress,
    ILogger<HeatmapGenerationService> logger,
    ITrackReader trackReader,
    IVisibilityService visibility,
    TimeProvider clock) : IHeatmapGenerationService
{
    public const string CountLogLayer = "count_log";
    public const int DefaultMaxZoom = 16;

    // Persisted to Heatmap.Error (and shown to friends'/public-segment viewers) when generation failed for a
    // reason whose exception text is NOT curated — a raw provider/IO/programming exception. The real detail
    // is logged server-side; only this generic string ever reaches the viewer (API-04).
    public const string GenericFailureMessage = "Heatmap generation failed. Please try regenerating it.";

    // ITrackReader is required (BlobStore: the row-store decode fallback is gone). Production DI injects
    // the shared cached singleton (span slices for a segment's efforts hit tracks already decoded by
    // analysis). GPS point data lives in the blob and never travels through EF entities.
    private Task<DecodedTrack?> ReadTrackAsync(TrackId trackId, CancellationToken ct) =>
        trackReader.GetTrackAsync(trackId, ct);

    /// <summary>Cap fallback floor — below this the render is dropped rather than degraded further.</summary>
    public const int MinMaxZoom = 13;

    public async Task GenerateAsync(SegmentId segmentId, string userId, CancellationToken ct = default)
    {
        var heatmap = await context.Heatmaps.FirstOrDefaultAsync(h => h.SegmentId == segmentId, ct);
        if (heatmap == null)
        {
            logger.LogWarning("Heatmap generation: no Heatmap row for segment {SegmentId}", segmentId);
            return;
        }

        // Writer streams tiles to a sibling .tmp; a prior generation's committed file stays untouched
        // until CommitAsync's atomic rename. Disposal without commit deletes the .tmp.
        HeatmapTilePackWriter? writer = null;
        try
        {
            heatmap.Status = HeatmapStatuses.Running;
            heatmap.Error = null;
            await context.SaveChangesAsync(ct);

            var audience = await SegmentAudienceAsync(segmentId, ct);
            var spans = await ResolveEffortSpansAsync(segmentId, heatmap, userId, audience, ct);
            if (spans.Count == 0)
                // Curated: this message is meant for the viewer (it tells them how to fix it).
                throw new HeatmapGenerationException("No efforts for this segment — run analysis first.");

            var (grid, zMax) = await PaintWithCapFallbackAsync(spans, userId, ct);
            var trackIds = spans.Select(s => s.TrackId).Distinct().ToList();

            writer = tileStore.BeginWrite(heatmap.HeatmapId);
            var meta = await builder.BuildAsync(
                grid, zMax,
                (z, tiles) => writer.AppendAsync(CountLogLayer, tiles, ct),
                z => progress.UpdateProgress(userId, $"Rendering z={z}"),
                ct);

            // SEC-01: a widen that landed mid-render found this row running, so the withdraw skipped it — these
            // tiles were cut for the old, smaller audience and must not go live.
            if (HeatmapPaintScope.Widens(audience.Visibility, (await SegmentAudienceAsync(segmentId, ct)).Visibility))
                throw new HeatmapGenerationException(HeatmapPaintScope.WidenedMessage);
            await writer.CommitAsync(ct);

            heatmap.Status = HeatmapStatuses.Done;
            heatmap.Error = null;
            heatmap.MinZoom = meta.MinZoom;
            heatmap.MaxZoom = meta.MaxZoom;
            heatmap.CountMax = meta.CountMax;
            heatmap.BoundsSouth = meta.South;
            heatmap.BoundsWest = meta.West;
            heatmap.BoundsNorth = meta.North;
            heatmap.BoundsEast = meta.East;
            heatmap.ActivityIds = string.Join(",", trackIds);
            heatmap.GeneratedAt = clock.GetUtcNow().UtcDateTime;
            await context.SaveChangesAsync(ct);

            logger.LogInformation(
                "Heatmap {HeatmapId} (segment {SegmentId}) done: z={Min}-{Max}, {Tracks} tracks",
                heatmap.HeatmapId, segmentId, meta.MinZoom, meta.MaxZoom, trackIds.Count);
        }
        catch (OperationCanceledException)
        {
            await MarkFailedAsync(heatmap, "Cancelled"); // curated, viewer-safe
            throw; // worker marks the in-memory progress as cancelled
        }
        catch (HeatmapGenerationException ex)
        {
            // Curated failure: the message was authored to be shown to a viewer, so persist it verbatim.
            logger.LogWarning("Heatmap generation stopped for segment {SegmentId}: {Reason}", segmentId, ex.Message);
            await MarkFailedAsync(heatmap, ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            // API-04: any other failure (raw provider/IO/programming exception) must NOT leak its message to
            // friends'/public-segment viewers via the poll endpoint. Log the detail server-side; persist only
            // the generic string.
            logger.LogError(ex, "Heatmap generation failed for segment {SegmentId}", segmentId);
            await MarkFailedAsync(heatmap, GenericFailureMessage);
            throw;
        }
        finally
        {
            // No-op if committed; otherwise drops the partial .tmp.
            if (writer != null) await writer.DisposeAsync();
        }
    }

    // ── Effort resolution ────────────────────────────────────────────────────

    /// <summary>
    /// One paint span per effort row (GpsInSegments), spans read straight from storage (MatchMode
    /// already baked in). An empty/null activity-type filter paints all efforts; otherwise efforts are
    /// restricted to activities of the requested types via the Activity nav join.
    /// </summary>
    private async Task<List<EffortSpan>> ResolveEffortSpansAsync(
        SegmentId segmentId, Heatmap heatmap, string userId, SegmentAudience audience, CancellationToken ct)
    {
        progress.UpdateProgress(userId, "Resolving efforts...");

        // SEC-01: paint only rides every viewer of the segment may see (HeatmapPaintScope). Also covers HM-16:
        // residue efforts of ex-friends fall out because friendship is re-read here. The worker scope sets the
        // user override to the job owner, who is the segment owner (attach is owner-only).
        var friendIds = (await visibility.GetVisibleUserIdsAsync()).Where(id => id != audience.OwnerId).ToList();
        var paintable = context.Activities
            .Where(HeatmapPaintScope.For(audience.Visibility, audience.OwnerId, friendIds));

        var query = context.GpsInSegments.AsNoTracking()
            .Where(g => g.SegmentId == segmentId)
            .Where(g => paintable.Any(a => a.TrackId == g.TrackId))
            // Item 25: don't paint efforts the user durably excluded (read-time anti-join). Repaint only
            // happens on regen, so excluding marks the heatmap stale (SegmentEfforts.RemoveEffortAsync).
            .Where(g => !context.SegmentEffortExclusions.Any(x =>
                x.SegmentId == g.SegmentId && x.TrackId == g.TrackId &&
                x.EntryIndex == (g.GpsIdEntryCalculated ?? g.GpsIdEntryGuessed)));

        var types = ParseCsv(heatmap.ActivityTypes);
        if (types is { Count: > 0 })
            query = query.Where(g =>
                g.Activity != null && g.Activity.ActivityType != null && types.Contains(g.Activity.ActivityType));

        var rows = await query.ToListAsync(ct);
        return rows
            .Select(r => { var (entry, exit) = r.ResolveGpsRange(); return new EffortSpan(r.TrackId.Value, entry, exit); })
            .Where(s => s.Entry.HasValue && s.Exit.HasValue)
            .ToList();
    }

    private sealed record SegmentAudience(string OwnerId, string Visibility);

    /// <summary>Who the tiles will be served to: the segment's owner and visibility, read fresh.</summary>
    private async Task<SegmentAudience> SegmentAudienceAsync(SegmentId segmentId, CancellationToken ct)
    {
        var seg = await context.Segments.AsNoTracking()
            .Where(s => s.SegmentId == segmentId)
            .Select(s => new { s.UserId, s.Visibility })
            .FirstOrDefaultAsync(ct);
        if (seg?.UserId is not { } ownerId)
            throw new HeatmapGenerationException("Segment not found.");
        return new SegmentAudience(ownerId, seg.Visibility);
    }

    // ── Painting ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Paint at z_max; on the tile-cap guardrail drop z_max one level and repaint, down to
    /// <see cref="MinMaxZoom"/>. Tracks come from TrackReader's cache; a cap retry re-slices them
    /// in memory rather than re-querying GPS.
    /// </summary>
    private async Task<(SparseTileGrid Grid, int ZMax)> PaintWithCapFallbackAsync(
        List<EffortSpan> spans, string userId, CancellationToken ct)
    {
        int zMax = DefaultMaxZoom;
        while (true)
        {
            try
            {
                return (await PaintAsync(spans, zMax, userId, ct), zMax);
            }
            catch (TileCapExceededException) when (zMax > MinMaxZoom)
            {
                logger.LogWarning("Heatmap tile cap exceeded at z={ZMax}; retrying at z={Next}", zMax, zMax - 1);
                zMax--;
            }
            catch (TileCapExceededException ex)
            {
                // Even at the floor zoom the segment paints too many tiles. Curated, viewer-safe signal (this
                // segment is too large/dense to render) — not an internal detail to redact to generic.
                throw new HeatmapGenerationException(
                    "This segment covers too large an area to render a heatmap.", ex);
            }
        }
    }

    private async Task<SparseTileGrid> PaintAsync(List<EffortSpan> spans, int zMax, string userId, CancellationToken ct)
    {
        var grid = new SparseTileGrid();
        for (int i = 0; i < spans.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress.UpdateProgress(userId, "Painting efforts...", i + 1, spans.Count);

            var span = spans[i];
            var track = await ReadTrackAsync(new TrackId(span.TrackId), ct);
            if (track == null) continue;                                    // was: pts.Count == 0
            int lo = 0, hi = track.Count - 1;
            if (span.Entry.HasValue && span.Exit.HasValue)
                (lo, hi) = track.OrdinalRange(span.Entry.Value, span.Exit.Value);   // ≡ SQL BETWEEN inclusive

            // Compact copy skipping NaN coords: today's SQL drops NULL rows before painting so the
            // polyline connects across them; passing NaN through would break it (TilePainter.cs:25-28).
            int cap = Math.Max(0, hi - lo + 1);
            var lats = new List<double>(cap);
            var lons = new List<double>(cap);
            for (int p = lo; p <= hi; p++)
            {
                double la = track.Lat[p], ln = track.Lng[p];
                if (!double.IsNaN(la) && !double.IsNaN(ln))
                {
                    lats.Add(la);
                    lons.Add(ln);
                }
            }
            if (lats.Count == 0) continue;
            TilePainter.PaintTrack(grid, lats, lons, zMax);
        }
        return grid;
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private async Task MarkFailedAsync(Heatmap heatmap, string error)
    {
        try
        {
            // Drop any previously-committed tile file so a failed heatmap serves nothing stale.
            tileStore.Delete(heatmap.HeatmapId);
            heatmap.Status = HeatmapStatuses.Failed;
            heatmap.Error = error;
            await context.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to record heatmap failure for heatmap {HeatmapId}", heatmap.HeatmapId);
        }
    }

    private static List<string>? ParseCsv(string? csv) =>
        csv == null ? null : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
