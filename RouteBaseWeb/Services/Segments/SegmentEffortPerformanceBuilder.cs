using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using RouteBaseWeb.Constants;
using RouteBaseWeb.Helpers;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Tracks;

namespace RouteBaseWeb.Services.Segments;

/// <summary>
/// Builds per-point effort performance data (the chart/replay payload) from efforts the caller has
/// ALREADY authorized. Extracted from <see cref="SegmentEfforts"/> so the authenticated page and the
/// share path run identical GPS→wire math.
///
/// Holds no identity and performs no visibility check — the gate is the only thing that differs between
/// the two callers, so it is the only thing left outside. Nor does it cache its RESULTS: each caller
/// caches under its own key family, which is what keeps the share path's entries out of the owner's
/// namespace (see CacheKeys.ShareSegment*).
/// </summary>
public interface ISegmentEffortPerformanceBuilder
{
    /// <param name="authorized">
    /// (effort row, its activity) pairs the caller has already authorized. Passing an unauthorized pair
    /// here is a caller bug that this class cannot detect — that is the deliberate trade for one shared
    /// implementation of the math.
    /// </param>
    Task<List<EffortPerformanceData>> BuildAsync(
        SegmentId segmentId,
        IReadOnlyList<(GpsInSegment Row, RouteBaseWeb.Models.Activity Activity)> authorized,
        int maxPointsPerEffort,
        CancellationToken ct = default);
}

public sealed class SegmentEffortPerformanceBuilder(
    RouteBaseDbContext context,
    IMemoryCache cache,
    SegmentCache segCache,
    IProfileSettingsService settings,
    ITrackReader trackReader) : ISegmentEffortPerformanceBuilder
{
    // Default rider+bike mass (kg) when a user has no RiderWeightKg setting. Only affects
    // estimated efforts (no power meter). Comes from the shared const, not a local literal —
    // it was a private copy that nothing related to the seeded value it had to match.
    // static readonly, not const: a decimal→double cast is not a constant expression in C#.
    private static readonly double DefaultRiderMassKg = (double)ProfileSettingsDefaults.RiderWeightKg;

    public async Task<List<EffortPerformanceData>> BuildAsync(
        SegmentId segmentId,
        IReadOnlyList<(GpsInSegment Row, RouteBaseWeb.Models.Activity Activity)> authorized,
        int maxPointsPerEffort,
        CancellationToken ct = default)
    {
        var result = new List<EffortPerformanceData>(authorized.Count);
        if (authorized.Count == 0) return result;

        // Captured at the guard (H2). Also closes a latent hole: the old filter checked only
        // `entry`, then dereferenced `exit!` — an entry-without-exit row now drops instead of NRE-ing.
        var ranges = new List<(GpsInSegment Row, RouteBaseWeb.Models.Activity Activity, int Entry, int Exit)>(authorized.Count);
        foreach (var (row, activity) in authorized)
            if (row.ResolveGpsRange() is { entry: int entryId, exit: int exitId })
                ranges.Add((row, activity, entryId, exitId));
        if (ranges.Count == 0) return result;

        // Reference polyline (per-point chainage) and rider mass — shared/memoized across the chunk.
        var segRef     = await GetSegmentReferenceAsync(segmentId);
        var massByUser = new Dictionary<string, double>(StringComparer.Ordinal);

        // One decode per track (LRU-shared with segment matching / heatmap / every other consumer),
        // then slice each effort's GPS_ID window straight out of the ordinal arrays. An effort touching
        // a track already in the LRU costs no I/O.
        foreach (var group in ranges.GroupBy(x => x.Row.TrackId))
        {
            TrackId trackId = group.Key;
            var track   = await trackReader.GetTrackAsync(trackId, ct);
            if (track == null) continue;

            foreach (var (row, activity, entryId, exitId) in group)
            {
                var (lo, hi) = track.OrdinalRange(entryId, exitId);
                var slice    = GpsSlice.FromDecoded(track, trackId, lo, hi);
                if (slice.Count == 0) continue;

                double mass    = await GetRiderMassAsync(activity.UserId, massByUser);
                var    points  = BuildPerformancePoints(slice, activity.ActivityType, mass, segRef, out bool costEstimated);
                double elapsed = slice[0].DateTimeUtc is { } sliceStart && slice[^1].DateTimeUtc is { } sliceEnd
                    ? (sliceEnd - sliceStart).TotalSeconds
                    : 0;

                // Downsample AFTER the full-slice build: cumulative distance/elapsed/cost/progress are
                // computed over every point, then kept points are selected by uniform stride and
                // columnarized into the wire form in one pass.
                var  kept      = EffortPointSampler.SampleIndices(points.Count, maxPointsPerEffort);
                bool truncated = kept.Length < points.Count;

                result.Add(new EffortPerformanceData
                {
                    EffortId        = row.GpsInSegmentId.Value,
                    ActivityId      = row.TrackId.Value,
                    ActivityName    = activity.TrackName ?? "Unknown",
                    // Day-at-midnight wall-clock ISO (parses as LOCAL in the browser) — bare
                    // yyyy-MM-dd parses as UTC midnight and shifted a day for UTC-minus viewers.
                    ActivityDate    = (activity.StartDateTimeLocal?.Date).ToWallClockIso(),
                    ElapsedTime     = elapsed,
                    UserId          = activity.UserId,
                    UserDisplayName = activity.User?.DisplayName ?? activity.UserId,
                    CostEstimated   = costEstimated,
                    CostKind        = costEstimated ? CostKinds.Estimated : CostKinds.Measured,
                    PointsTruncated = truncated,
                    Points          = EffortPointsWire.FromPoints(points, kept)
                });
            }
        }
        return result;
    }

    /// <summary>
    /// Resolves the effort owner's rider mass, memoized per user for the request. Takes the id as a
    /// parameter rather than reading the ambient identity, so a share render reads the grant owner's
    /// setting and never a third party's.
    /// </summary>
    private async Task<double> GetRiderMassAsync(string? ownerUserId, Dictionary<string, double> memo)
    {
        var key = ownerUserId ?? string.Empty;
        if (memo.TryGetValue(key, out var mass)) return mass;

        mass = await settings.GetSettingAsync(
            SettingCategories.Performance, SettingKeys.RiderWeightKg, ownerUserId, DefaultRiderMassKg);
        // Clamped through the catalog, which is now also what the settings form renders its min/max from.
        // The old guard here was `mass <= 0` — a second, looser rule for the same setting, so a stored
        // 5000 kg was "valid" to this reader and rejected by the form. One rule or the drift is back.
        mass = (double)SettingsCatalog.RiderWeightKg.Clamp((decimal)mass);
        memo[key] = mass;
        return mass;
    }

    // SE-01: a leg with any missing endpoint coordinate contributes zero distance, not a haversine to
    // Null Island (0,0). GpsSlice legitimately maps a channel-NaN coordinate → null (FIT timestamp-only
    // records, GPS dropouts); without this guard one coordinate-less point adds thousands of km to the
    // cumulative distance — and, via the fallback path, to every point after it.
    private static double HaversineLeg(GpsSlice prev, GpsSlice curr) =>
        prev.GpsLat.HasValue && prev.GpsLong.HasValue && curr.GpsLat.HasValue && curr.GpsLong.HasValue
            ? GeoUtils.HaversineDistance(prev.GpsLat.Value, prev.GpsLong.Value, curr.GpsLat.Value, curr.GpsLong.Value)
            : 0;

    private static List<EffortDataPoint> BuildPerformancePoints(
        List<GpsSlice> pts, string? activityType, double riderMassKg,
        SegmentChainage.SegmentReference? segRef, out bool costEstimated)
    {
        var first  = pts[0];
        var result = new List<EffortDataPoint>(pts.Count);
        double cumDist = 0;

        var cost = EffortCostCalculator.Compute(pts, activityType, riderMassKg);
        costEstimated = cost.Estimated;

        for (int i = 0; i < pts.Count; i++)
        {
            var curr = pts[i];

            double elapsed = 0;
            // UTC is the only per-point timestamp (GPS.datetime_loc dropped — SL-01).
            if (first.DateTimeUtc.HasValue && curr.DateTimeUtc.HasValue)
                elapsed = (curr.DateTimeUtc.Value - first.DateTimeUtc.Value).TotalSeconds;

            if (i > 0)
            {
                var prev = pts[i - 1];
                cumDist = (curr.Distance.HasValue && prev.Distance.HasValue && first.Distance.HasValue)
                    ? curr.Distance.Value - first.Distance.Value
                    : cumDist + HaversineLeg(prev, curr);
            }

            result.Add(new EffortDataPoint
            {
                Distance         = Math.Round(cumDist, 1),
                Time             = Math.Round(elapsed, 1),
                Speed            = Math.Round((curr.Speed ?? 0) * 3.6, 2),
                Altitude         = curr.Altitude,
                Lat              = curr.GpsLat,
                Lng              = curr.GpsLong,
                CumulativeCostKj = Math.Round(cost.CumulativeKj[i], 3)
            });
        }

        // Per-point segment progress (chainage). Left null when geometry is unavailable
        // so the frontend falls back to per-effort distance normalization.
        if (segRef != null)
            SegmentChainage.AssignSegmentProgress(pts, result, segRef);

        return result;
    }

    /// <summary>
    /// Loads (and caches) the segment's full-resolution reference polyline as a
    /// <see cref="SegmentChainage.SegmentReference"/> for per-point progress projection. Returns null
    /// when the segment has fewer than 2 GPS points / zero length.
    ///
    /// This IS cached inside the builder — unlike the per-effort results — because SA-12 makes it
    /// user-independent geometry stored under a token-less shared entry, so both callers may safely
    /// share one copy.
    /// </summary>
    private async Task<SegmentChainage.SegmentReference?> GetSegmentReferenceAsync(SegmentId segmentId)
    {
        var cacheKey = CacheKeys.SegmentReference(segmentId);
        if (cache.TryGetValue(cacheKey, out SegmentChainage.SegmentReference? cached))
            return cached;

        var points = await context.SegmentGps
            .Where(sg => sg.SegmentId == segmentId && sg.GpsLat != null && sg.GpsLong != null)
            .OrderBy(sg => sg.SegGpsId)
            .Select(sg => new SegmentGpsPoint
            {
                // The Where above filters nulls; `!` kept deliberately (H2 floor) — .Value and casts
                // both warn CS8629 inside the expression tree, and ?? 0 would invent Null Island.
                Lat      = sg.GpsLat!.Value,
                Lng      = sg.GpsLong!.Value,
                Altitude = sg.Altitude
            })
            .ToListAsync();

        var segRef = SegmentChainage.BuildSegmentReference(points);
        if (segRef != null)
            // SA-12: the reference is user-independent geometry, keyed by segmentId only → store with a
            // token-less shared entry so one viewer's InvalidateUser can't evict it for everyone.
            cache.Set(cacheKey, segRef, segCache.SharedOptions(SegmentCache.LongTtl));
        return segRef;
    }
}
