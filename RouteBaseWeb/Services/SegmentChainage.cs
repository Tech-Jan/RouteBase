using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services;

/// <summary>
/// Per-point "segment progress" (chainage / linear referencing) — owned by <see cref="SegmentEfforts"/>.
///
/// Maps each effort GPS point to a fraction (0–1) of the segment's reference
/// polyline so the chart's "% Complete" x-axis reflects position ALONG THE SEGMENT
/// rather than distance travelled in the effort.
///
/// Uses a global, band-constrained Dynamic Time Warping (DTW) alignment between the
/// effort point sequence and a chainage-annotated resampling of the segment. Unlike a
/// greedy forward projection, DTW is globally monotonic and reconsiders the whole
/// sequence, so an effort that shortcuts a bend (and geographically passes near a LATER
/// part of the polyline) cannot get "stuck" or jump ahead — the shortcut is spread across
/// the skipped chainage instead of collapsing to a vertical line + jump to the finish.
/// Genuinely off-segment spans (detour/shortcut, deviation &gt; <see cref="OnSegmentThresholdMeters"/>)
/// are re-interpolated by effort-distance between the bounding on-segment anchors so they
/// "meet back up" smoothly. The Sakoe-Chiba band is centred on the effort's own distance
/// progression, which also disambiguates self-crossing loops.
///
/// Pure, stateless geometry — no DB or cache access. Reuses
/// <see cref="GeoUtils.HaversineDistance"/> for arc lengths.
/// </summary>
internal static class SegmentChainage
{
    // --- Tunable knobs ---

    /// <summary>Perpendicular deviation (m) within which a DTW-aligned point is "on the segment".</summary>
    private const double OnSegmentThresholdMeters = 25.0;

    /// <summary>Target spacing (m) between resampled segment chainage samples.</summary>
    private const double SegResampleMeters = 8.0;

    /// <summary>Cap on resampled segment samples (bounds DTW width for very long segments).</summary>
    private const int MaxSegmentSamples = 1500;

    /// <summary>Cap on effort points fed into the DTW grid (bounds DTW height for dense efforts).</summary>
    private const int MaxEffortGrid = 600;

    /// <summary>Sakoe-Chiba band radius as a fraction of the segment sample count.</summary>
    private const double DtwBandFraction = 0.4;

    /// <summary>Floor for the band radius (samples), so short segments still warp.</summary>
    private const int MinBandSamples = 12;

    private const double ProgressEarthRadiusMeters = 6371000.0;

    /// <summary>
    /// Precomputed segment geometry for fast point-to-polyline alignment.
    /// Vertices live on a local equirectangular plane (metres); <see cref="CumLen"/>
    /// holds haversine cumulative arc length (CumLen[0] == 0). <see cref="Sx"/>/<see cref="Sy"/>/
    /// <see cref="Chain"/> are a uniform chainage resampling, built eagerly in
    /// <see cref="BuildSegmentReference"/> (SE-05: the instance is cache-shared across concurrent
    /// chart requests, so a lazily-assigned multi-word field would risk a torn read).
    /// </summary>
    internal sealed class SegmentReference
    {
        public required double[] X;       // local-plane east (m)
        public required double[] Y;       // local-plane north (m)
        public required double[] CumLen;  // cumulative arc length (m), CumLen[0] = 0
        public required double TotalLen;  // = CumLen[^1]
        public required double Lat0;      // projection reference latitude (deg)
        public required double Lng0;      // projection reference longitude (deg)
        public required double CosLat0;   // cos(Lat0)

        // Uniform chainage resampling of the polyline (per-sample plane coords + chainage), built once
        // at construction. Immutable init-only arrays → safe to read from any thread.
        public required double[] Sx;      // resample plane east (m)
        public required double[] Sy;      // resample plane north (m)
        public required double[] Chain;   // resample chainage (m)

        public int Count => CumLen.Length;

        public double ToLocalX(double lng) =>
            ProgressEarthRadiusMeters * GeoUtils.ToRadians(lng - Lng0) * CosLat0;

        public double ToLocalY(double lat) =>
            ProgressEarthRadiusMeters * GeoUtils.ToRadians(lat - Lat0);
    }

    /// <summary>
    /// Builds a <see cref="SegmentReference"/> from the segment's ordered polyline.
    /// Returns null when there are fewer than 2 points or the polyline has zero length.
    /// </summary>
    internal static SegmentReference? BuildSegmentReference(IReadOnlyList<SegmentGpsPoint> pts)
    {
        if (pts is null || pts.Count < 2) return null;

        int n = pts.Count;
        double lat0 = 0;
        for (int i = 0; i < n; i++) lat0 += pts[i].Lat;
        lat0 /= n;
        double lng0 = pts[0].Lng;
        double cosLat0 = Math.Cos(GeoUtils.ToRadians(lat0));

        var x = new double[n];
        var y = new double[n];
        var cum = new double[n];

        for (int i = 0; i < n; i++)
        {
            x[i] = ProgressEarthRadiusMeters * GeoUtils.ToRadians(pts[i].Lng - lng0) * cosLat0;
            y[i] = ProgressEarthRadiusMeters * GeoUtils.ToRadians(pts[i].Lat - lat0);
        }

        cum[0] = 0;
        for (int i = 1; i < n; i++)
            cum[i] = cum[i - 1] + GeoUtils.HaversineDistance(
                pts[i - 1].Lat, pts[i - 1].Lng, pts[i].Lat, pts[i].Lng);

        double total = cum[n - 1];
        if (total <= 0) return null;

        var (sx, sy, chain) = ResampleByChainage(x, y, cum, total);

        return new SegmentReference
        {
            X = x, Y = y, CumLen = cum, TotalLen = total,
            Lat0 = lat0, Lng0 = lng0, CosLat0 = cosLat0,
            Sx = sx, Sy = sy, Chain = chain,
        };
    }

    /// <summary>
    /// Computes per-point <see cref="EffortDataPoint.SegmentProgress"/> (0–1) for one
    /// effort via band-constrained DTW alignment. <paramref name="built"/> already carries
    /// the effort's cumulative <see cref="EffortDataPoint.Distance"/>; <paramref name="effortPts"/>
    /// supplies the raw lat/lng (1:1 with <paramref name="built"/>).
    /// </summary>
    internal static void AssignSegmentProgress(
        List<GpsSlice> effortPts, List<EffortDataPoint> built, SegmentReference seg)
    {
        int n = built.Count;
        if (n == 0 || effortPts.Count != n) return;

        double[] sx = seg.Sx, sy = seg.Sy, schain = seg.Chain;
        int m = schain.Length;
        if (m < 2) { for (int i = 0; i < n; i++) built[i].SegmentProgress = 0; return; }

        // Coordinate-bearing effort points → a plane track parameterised by effort distance.
        var td = new List<double>(n);
        var tx = new List<double>(n);
        var ty = new List<double>(n);
        for (int i = 0; i < n; i++)
        {
            var p = effortPts[i];
            if (!p.GpsLat.HasValue || !p.GpsLong.HasValue) continue;
            td.Add(built[i].Distance);
            tx.Add(seg.ToLocalX(p.GpsLong.Value));
            ty.Add(seg.ToLocalY(p.GpsLat.Value));
        }
        if (td.Count == 0) { for (int i = 0; i < n; i++) built[i].SegmentProgress = 0; return; }

        // Resample to ~segment spacing so the two sequences have comparable density (a DTW
        // diagonal step then means "both advance ~equal distance"; up/left moves absorb
        // local pace differences from detours/shortcuts).
        var (gx, gy, gdist) = ResampleTrack(td, tx, ty);
        int g = gx.Length;

        RunDtw(gx, gy, gdist, sx, sy, schain, out var gridChain, out var gridDev);

        // Off-segment spans (deviation > threshold) → interpolate chainage by effort distance
        // between the bounding on-segment anchors so a detour/shortcut "meets back up".
        var mg = InterpolateOffSegment(gridChain, gridDev, gdist);

        // Grid-level monotonic clamp.
        double gRun = 0;
        for (int k = 0; k < g; k++) { gRun = Math.Max(gRun, mg[k]); mg[k] = gRun; }

        // Map grid chainage to every effort point by effort distance, then normalize (monotonic).
        int gpos = 0;
        double run = 0;
        for (int i = 0; i < n; i++)
        {
            double d = built[i].Distance;
            while (gpos < g - 2 && gdist[gpos + 1] < d) gpos++;

            double chain;
            if (d <= gdist[0]) chain = mg[0];
            else if (d >= gdist[g - 1]) chain = mg[g - 1];
            else
            {
                double span = gdist[gpos + 1] - gdist[gpos];
                double t = span > 0 ? (d - gdist[gpos]) / span : 0;
                chain = mg[gpos] + (mg[gpos + 1] - mg[gpos]) * t;
            }

            run = Math.Max(run, chain);
            built[i].SegmentProgress = Math.Clamp(run / seg.TotalLen, 0.0, 1.0);
        }
    }

    /// <summary>Resamples the effort track (plane coords parameterised by effort distance) at
    /// ~<see cref="SegResampleMeters"/> spacing, capped by <see cref="MaxEffortGrid"/> points.</summary>
    private static (double[] Gx, double[] Gy, double[] Gdist) ResampleTrack(
        List<double> d, List<double> x, List<double> y)
    {
        int c = d.Count;
        double total = d[c - 1] - d[0];
        if (c == 1 || total <= 0)
            return (new[] { x[0] }, new[] { y[0] }, new[] { d[0] });

        double spacing = Math.Max(SegResampleMeters, total / MaxEffortGrid);
        int g = Math.Max(2, (int)Math.Ceiling(total / spacing) + 1);

        var gx = new double[g];
        var gy = new double[g];
        var gd = new double[g];

        int seg = 0;
        for (int k = 0; k < g; k++)
        {
            double target = k == g - 1 ? d[c - 1] : d[0] + Math.Min(total, k * spacing);
            while (seg < c - 2 && d[seg + 1] < target) seg++;
            double dl = d[seg + 1] - d[seg];
            double t = dl > 0 ? (target - d[seg]) / dl : 0;
            gx[k] = x[seg] + t * (x[seg + 1] - x[seg]);
            gy[k] = y[seg] + t * (y[seg + 1] - y[seg]);
            gd[k] = target;
        }
        return (gx, gy, gd);
    }

    /// <summary>
    /// Band-constrained, open-begin/open-end DTW aligning effort grid points (gx,gy,gdist)
    /// to segment samples (sx,sy,schain). Emits per-grid-point chainage and deviation (m).
    /// The band is centred on the effort's own distance progression, so partial efforts
    /// (sub-range) and self-crossing loops align correctly.
    /// </summary>
    private static void RunDtw(
        double[] gx, double[] gy, double[] gdist,
        double[] sx, double[] sy, double[] schain,
        out double[] gridChain, out double[] gridDev)
    {
        int g = gx.Length, m = sx.Length;
        gridChain = new double[g];
        gridDev = new double[g];

        double distSpan = gdist[g - 1] - gdist[0];
        double radius = Math.Max(MinBandSamples, DtwBandFraction * (m - 1));

        double Center(int i) => (distSpan > 0 ? (gdist[i] - gdist[0]) / distSpan : 0) * (m - 1);
        int Lo(int i) => Math.Max(0, (int)Math.Floor(Center(i) - radius));
        int Hi(int i) => Math.Min(m - 1, (int)Math.Ceiling(Center(i) + radius));

        double Cost(int i, int j)
        {
            double dx = gx[i] - sx[j], dy = gy[i] - sy[j];
            return dx * dx + dy * dy;
        }

        var prev = new double[m];
        var cur = new double[m];
        var back = new byte[g * m]; // 0 diag, 1 up, 2 left, 3 start
        Array.Fill(prev, double.PositiveInfinity);

        int lo0 = Lo(0), hi0 = Hi(0);
        for (int j = lo0; j <= hi0; j++) { prev[j] = Cost(0, j); back[j] = 3; }

        for (int i = 1; i < g; i++)
        {
            Array.Fill(cur, double.PositiveInfinity);
            int lo = Lo(i), hi = Hi(i), plo = Lo(i - 1), phi = Hi(i - 1);
            for (int j = lo; j <= hi; j++)
            {
                double best = double.PositiveInfinity; byte move = 3;
                if (j >= plo && j <= phi && prev[j] < best) { best = prev[j]; move = 1; }      // up
                if (j - 1 >= plo && j - 1 <= phi && prev[j - 1] < best) { best = prev[j - 1]; move = 0; } // diag
                if (j - 1 >= lo && cur[j - 1] < best) { best = cur[j - 1]; move = 2; }          // left
                if (double.IsPositiveInfinity(best)) continue;
                cur[j] = best + Cost(i, j);
                back[i * m + j] = move;
            }
            (prev, cur) = (cur, prev);
        }

        // Open-end: pick the least-cost sample on the last row.
        int lastLo = Lo(g - 1), lastHi = Hi(g - 1), jstar = lastLo;
        double bestEnd = double.PositiveInfinity;
        for (int j = lastLo; j <= lastHi; j++)
            if (prev[j] < bestEnd) { bestEnd = prev[j]; jstar = j; }

        // Backtrack; record the furthest sample matched at each grid point (first seen).
        var seen = new bool[g];
        int ci = g - 1, cj = jstar;
        while (ci >= 0)
        {
            if (!seen[ci])
            {
                gridChain[ci] = schain[cj];
                double dx = gx[ci] - sx[cj], dy = gy[ci] - sy[cj];
                gridDev[ci] = Math.Sqrt(dx * dx + dy * dy);
                seen[ci] = true;
            }
            byte b = back[ci * m + cj];
            if (b == 3) break;             // start (row 0)
            if (b == 0) { ci--; cj--; }    // diag
            else if (b == 1) { ci--; }     // up
            else { cj--; }                 // left (same grid point)
        }
        for (int i = 0; i < g; i++)
            if (!seen[i]) { gridChain[i] = i > 0 ? gridChain[i - 1] : 0; gridDev[i] = double.MaxValue; }
    }

    /// <summary>
    /// Replaces off-segment grid chainages (deviation &gt; <see cref="OnSegmentThresholdMeters"/>)
    /// with an effort-distance interpolation between the bounding on-segment anchors. Leading
    /// off-segment points ramp from chainage 0; trailing ones keep their aligned chainage.
    /// </summary>
    private static double[] InterpolateOffSegment(double[] gridChain, double[] gridDev, double[] gdist)
    {
        int g = gridChain.Length;
        var mg = (double[])gridChain.Clone();

        int first = -1, last = -1;
        for (int k = 0; k < g; k++)
            if (gridDev[k] <= OnSegmentThresholdMeters) { if (first < 0) first = k; last = k; }

        if (first < 0) return mg; // no anchors: fall back to aligned chainage

        for (int k = 0; k < first; k++)
            mg[k] = Interpolate(gdist[0], 0, gdist[first], gridChain[first], gdist[k]);

        int prev = first;
        for (int k = first + 1; k <= last; k++)
        {
            if (gridDev[k] > OnSegmentThresholdMeters) continue;
            if (k > prev + 1)
                for (int j = prev + 1; j < k; j++)
                    mg[j] = Interpolate(gdist[prev], gridChain[prev], gdist[k], gridChain[k], gdist[j]);
            mg[k] = gridChain[k];
            prev = k;
        }
        return mg;
    }

    /// <summary>Resamples the polyline at ~<see cref="SegResampleMeters"/> spacing (capped by
    /// <see cref="MaxSegmentSamples"/>), returning plane coords + chainage per sample.</summary>
    private static (double[] Sx, double[] Sy, double[] Chain) ResampleByChainage(
        double[] x, double[] y, double[] cumLen, double totalLen)
    {
        int count = cumLen.Length;
        double spacing = Math.Max(SegResampleMeters, totalLen / MaxSegmentSamples);
        int m = Math.Max(2, (int)Math.Ceiling(totalLen / spacing) + 1);

        var sx = new double[m];
        var sy = new double[m];
        var chain = new double[m];

        int edge = 0;
        for (int k = 0; k < m; k++)
        {
            double target = k == m - 1 ? totalLen : Math.Min(totalLen, k * spacing);
            while (edge < count - 2 && cumLen[edge + 1] < target) edge++;
            double edgeLen = cumLen[edge + 1] - cumLen[edge];
            double t = edgeLen > 0 ? (target - cumLen[edge]) / edgeLen : 0;
            sx[k] = x[edge] + t * (x[edge + 1] - x[edge]);
            sy[k] = y[edge] + t * (y[edge + 1] - y[edge]);
            chain[k] = target;
        }
        return (sx, sy, chain);
    }

    /// <summary>Linear interpolation of chainage by effort-distance fraction.</summary>
    private static double Interpolate(double dA, double mA, double dB, double mB, double d)
    {
        if (dB <= dA) return mB;
        double f = (d - dA) / (dB - dA);
        if (f < 0) f = 0; else if (f > 1) f = 1;
        return mA + (mB - mA) * f;
    }
}
