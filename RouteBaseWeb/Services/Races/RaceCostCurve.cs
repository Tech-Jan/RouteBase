using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Tracks;
using Microsoft.Extensions.Caching.Memory;

namespace RouteBaseWeb.Services.Races;

/// <summary>Seconds-per-metre by 1% grade bin. Monotone non-decreasing in grade, by construction.</summary>
public sealed record RaceCostCurve(int MinBin, double[] SecondsPerMetre);

public interface IRaceCostCurveService
{
    /// <summary>
    /// Fit the grade→cost curve from a REAL effort over this course, so the synthetic field inherits a
    /// real rider's terrain response instead of a constant someone typed in.
    ///
    /// <paramref name="effortTrackId"/> is the race's reference effort, falling back to the course
    /// activity — which used to be the ONLY option, on the theory that "a course is a ride you actually
    /// did". That theory breaks the moment a course is drawn or merged: such a track has no usable
    /// timestamps, nothing fits, and the field silently inherits a DEFAULT curve — a mountain-bike one,
    /// which then paces a relay's ski, canoe and kayak legs with bike physics. Naming any real effort
    /// over the course fixes it. See Race.ReferenceTrackId.
    ///
    /// Null when the effort carries no usable timing. The caller falls back to a default curve rather
    /// than fitting nonsense.
    ///
    /// <paramref name="effortSha"/> is the effort track's CONTENT sha, and it is required rather than
    /// optional on purpose: it is half of this curve's cache identity, and a default would let a caller
    /// silently reintroduce the staleness bug it exists to close. Null means "this effort has no content
    /// version" — the result is then not cached at all, exactly as RaceCourseService does with its grid.
    /// </summary>
    Task<RaceCostCurve?> FitFromEffortAsync(
        TrackId effortTrackId, string? effortSha, RaceCourseGrid grid, TrackId courseTrackId,
        CancellationToken ct = default);
}

/// <inheritdoc />
public class RaceCostCurveService : IRaceCostCurveService
{
    private readonly ITrackReader _tracks;
    private readonly IMemoryCache _cache;

    /// <summary>Window for the observed pace and the grade it is paired with — 100 m, as in the grid.</summary>
    private const int WindowNodes = 5;

    /// <summary>A bin's median means nothing below this many samples.</summary>
    private const int MinSamplesPerBin = 3;

    /// <summary>Absurd paces are gate crashes, not data: 90 km/h down, 1 km/h up.</summary>
    private const double MinSecondsPerMetre = 3.6 / 90.0;
    private const double MaxSecondsPerMetre = 3.6 / 1.0;

    // Matches the grid's TTL deliberately (RaceCourseService). The curve is a pure function of the same
    // course track, editing a course produces a NEW version rather than mutating one, and the two entries
    // expire together — so the pair cannot disagree, and the Set is bounded (never an unbounded cache).
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    /// <summary>Wraps the result so a cached "this course cannot be fitted" is not mistaken for a miss.</summary>
    private sealed record CachedCurve(RaceCostCurve? Curve);

    public RaceCostCurveService(ITrackReader tracks, IMemoryCache cache)
    {
        _tracks = tracks;
        _cache = cache;
    }

    public async Task<RaceCostCurve?> FitFromEffortAsync(
        TrackId effortTrackId, string? effortSha, RaceCourseGrid grid, TrackId courseTrackId,
        CancellationToken ct = default)
    {
        // BOTH ids are in the key. The curve is a function of the effort AND the course grid it is
        // sampled against, so keying on the course alone (as this did while the effort was always the
        // course) would serve one race's curve to another race over the same course with a different
        // reference effort — a pacing bug that looks like bad physics, not like a cache bug.
        //
        // And BOTH CONTENT SHAS are in it, because an id names a track, not its content:
        //
        //   grid.SourceSha — the curve is fitted to the grid's GRADES, so recalculating the COURSE's
        //     elevation must refit it. Under an id-only key it did not, and the field went on being paced
        //     by the pre-refresh terrain until the process restarted.
        //
        //   effortSha — the curve is fitted from the EFFORT's timestamps and positions. The moment a race
        //     could name an effort OTHER than its course (Race.ReferenceTrackId — the reason this method
        //     exists), the same hole opened on that axis: trimming, DEM-refreshing or reverting the
        //     reference ride moves ITS head object while the course's sha never changes, so the key held
        //     still and the whole field kept being paced by the pre-edit ride for the full TTL.
        var key = $"race-cost-curve:{effortTrackId}:{effortSha}:{courseTrackId}:{grid.SourceSha}";

        // No content identity, no cache — the same rule RaceCourseService applies to the grid. A grid it
        // cannot identify is rebuilt from live data every request; a curve cached under a key that cannot
        // vary would then be a stale curve pinned to a fresh grid, which is precisely the pair the TTL
        // note below promises cannot happen.
        var identified = effortSha != null && grid.SourceSha != null;

        if (identified && _cache.TryGetValue(key, out CachedCurve? cached) && cached != null)
            return cached.Curve;

        var curve = await FitAsync(effortTrackId, grid, ct);

        // The null is cached too: an un-fittable effort (synthesized timestamps, a merged ride whose
        // junction gaps dwarf the riding) would otherwise redo the whole fit on every page load.
        if (identified) _cache.Set(key, new CachedCurve(curve), CacheTtl);
        return curve;
    }

    private async Task<RaceCostCurve?> FitAsync(
        TrackId effortTrackId, RaceCourseGrid grid, CancellationToken ct)
    {
        var track = await _tracks.GetTrackAsync(effortTrackId, ct);
        if (track == null || track.Count < 2 || track.TimeEpochS.IsNull) return null;

        var arrival = ArrivalPerNode(track, grid);
        if (arrival == null) return null;

        var samples = new Dictionary<int, List<double>>();
        for (var i = 0; i < grid.Count; i++)
        {
            var lo = Math.Max(0, i - WindowNodes);
            var hi = Math.Min(grid.Count - 1, i + WindowNodes);
            var dd = grid.Distance[hi] - grid.Distance[lo];
            var dt = arrival[hi] - arrival[lo];
            if (dd <= 0 || dt <= 0) continue;

            var cost = dt / dd;
            // NaN is not a slow sample, it is NO sample — and the bounds below cannot reject it, because
            // every comparison with NaN is false. Waved through, it medians into a bin, PAVA spreads it
            // over the neighbours, and the curve ships as NaN — which System.Text.Json refuses to write,
            // killing the whole replay response mid-stream. It arrives here from the effort's per-point
            // NULL timestamps (TrackChannel reads those as NaN).
            if (!double.IsFinite(cost) || cost < MinSecondsPerMetre || cost > MaxSecondsPerMetre) continue;

            var bin = (int)Math.Floor(grid.GradePercent[i]);
            if (!samples.TryGetValue(bin, out var bucket)) samples[bin] = bucket = [];
            bucket.Add(cost);
        }

        var solid = samples.Where(kv => kv.Value.Count >= MinSamplesPerBin).ToList();
        if (solid.Count < 3) return null;   // too thin to be a curve

        var minBin = solid.Min(kv => kv.Key);
        var maxBin = solid.Max(kv => kv.Key);
        var median = solid.ToDictionary(kv => kv.Key, kv => Median(kv.Value));

        // Contiguous domain for the isotonic pass; interpolated bins carry almost no weight.
        var values = new List<double>();
        var weights = new List<double>();
        for (var bin = minBin; bin <= maxBin; bin++)
        {
            if (median.TryGetValue(bin, out var known))
            {
                values.Add(known);
                weights.Add(samples[bin].Count);
                continue;
            }
            var left = median.Keys.Where(k => k < bin).Max();
            var right = median.Keys.Where(k => k > bin).Min();
            var t = (double)(bin - left) / (right - left);
            values.Add(median[left] + t * (median[right] - median[left]));
            weights.Add(1);
        }

        return new RaceCostCurve(minBin, Pava(values, weights));
    }

    /// <summary>The reference rider's elapsed time at each course node.</summary>
    private static double[]? ArrivalPerNode(DecodedTrack track, RaceCourseGrid grid)
    {
        // The SAME point set the grid was built from.
        //
        // RaceCourseService DROPS a point with no fix before gridding, so the grid's chainage runs the
        // chord straight across the gap. This used to bridge that gap by adding ZERO metres instead —
        // "it costs only the metres actually spanned by the hole", said the comment, and it cost none. The
        // effort's arc therefore stopped advancing exactly where the grid's kept going, and the two ended
        // up on different scales: 1795 m against 2000 m for a 200 m hole.
        //
        // They are two measurements of ONE ride — the default reference effort IS the course track — so
        // every node past a hole was matched to an arrival from the wrong stretch of the ride, and the
        // curve those mismatched samples fit is what paces the entire field. On a 650 m hole the climb
        // bin fitted at 0.10 s/m instead of 0.40: the model believed he climbed at descent pace. Smaller
        // holes were absorbed by the per-bin median, which is why this survived — it degrades quietly.
        //
        // This course canoes and kayaks across water. The holes are the reason the NaN guards exist.
        var usable = new List<int>(track.Count);
        for (var i = 0; i < track.Count; i++)
        {
            if (double.IsFinite(track.Lat[i]) && double.IsFinite(track.Lng[i])) usable.Add(i);
        }
        if (usable.Count < 2) return null;

        var n = usable.Count;
        var arc = new double[n];
        var elapsed = new double[n];

        // The first timestamp can itself be a hole. Anchored on a NaN, every elapsed time is NaN and not
        // one sample survives the fit — so anchor on the first timestamp that actually exists.
        var t0 = double.NaN;
        for (var i = 0; i < n && double.IsNaN(t0); i++) t0 = track.TimeEpochS[usable[i]];
        if (double.IsNaN(t0)) return null;

        for (var i = 0; i < n; i++)
        {
            var src = usable[i];
            if (i > 0)
            {
                var prev = usable[i - 1];
                arc[i] = arc[i - 1] + GeoUtils.HaversineDistance(
                    track.Lat[prev], track.Lng[prev], track.Lat[src], track.Lng[src]);
            }
            elapsed[i] = track.TimeEpochS[src] - t0;   // a hole stays NaN; its samples drop out of the fit
        }
        // A NaN final timestamp must NOT reject the effort — the fit still has every finite sample before
        // it. Only a course with no distance, or with frozen (synthesized) timestamps, has nothing to teach.
        if (arc[^1] <= 0 || elapsed[^1] <= 0) return null;

        var arrival = new double[grid.Count];
        var j = 0;
        for (var i = 0; i < grid.Count; i++)
        {
            var d = grid.Distance[i];
            while (j < n - 2 && arc[j + 1] < d) j++;
            var span = arc[j + 1] - arc[j];
            var f = span <= 0 ? 0 : Math.Clamp((d - arc[j]) / span, 0, 1);
            arrival[i] = elapsed[j] + f * (elapsed[j + 1] - elapsed[j]);
        }
        return arrival;
    }

    /// <summary>
    /// Pool-adjacent-violators: the nearest monotone non-decreasing fit, weighted by sample count.
    ///
    /// Cost must not fall as the road tilts up. Raw binned medians violate that badly on the descent side
    /// (one technical switchback bin reads slower than the 20%-steeper bins either side), and an
    /// oscillating descent curve is what flings synthetic riders down the mountain. The descent SPEED
    /// CEILING then emerges by itself: the steep bins pool into a plateau instead of extrapolating ever
    /// faster, so there is no cap to hand-tune.
    /// </summary>
    internal static double[] Pava(IReadOnlyList<double> values, IReadOnlyList<double> weights)
    {
        var blocks = new List<(double Value, double Weight, int Count)>(values.Count);
        for (var i = 0; i < values.Count; i++) blocks.Add((values[i], weights[i], 1));

        var k = 0;
        while (k < blocks.Count - 1)
        {
            if (blocks[k].Value <= blocks[k + 1].Value + 1e-12) { k++; continue; }

            var a = blocks[k];
            var b = blocks[k + 1];
            var weight = a.Weight + b.Weight;
            blocks[k] = ((a.Value * a.Weight + b.Value * b.Weight) / weight, weight, a.Count + b.Count);
            blocks.RemoveAt(k + 1);
            if (k > 0) k--;
        }

        var result = new List<double>(values.Count);
        foreach (var block in blocks)
        {
            for (var i = 0; i < block.Count; i++) result.Add(block.Value);
        }
        return [.. result];
    }

    private static double Median(List<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
