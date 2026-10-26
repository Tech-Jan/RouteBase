namespace RouteBaseWeb.Services.Heatmaps;

/// <summary>
/// Paints GPS tracks into a <see cref="SparseTileGrid"/> at a fixed zoom.
/// Consecutive points are connected with Bresenham lines and each call
/// deduplicates pixels, so one track adds at most 1 per pixel — counts mean
/// "distinct passes", not "GPS samples" (dwell at a stop is no hotspot).
/// </summary>
public static class TilePainter
{
    /// <summary>Chebyshev px gap above which consecutive points are not connected (GPS dropout guard).</summary>
    public const int MaxSegmentPx = 512;

    public static void PaintTrack(SparseTileGrid grid, IReadOnlyList<double> lats, IReadOnlyList<double> lons, int zoom)
    {
        if (lats.Count != lons.Count)
            throw new ArgumentException("lats and lons must have equal length.");

        var painted = new HashSet<long>();
        bool hasPrev = false;
        int px = 0, py = 0;
        for (int i = 0; i < lats.Count; i++)
        {
            double lat = lats[i];
            // HM-01: reject non-finite/out-of-range coords — an Infinity lon would cast to a garbage
            // pixel at an absurd tile and silently push OccupiedTileCount toward the cap.
            if (!double.IsFinite(lat) || !double.IsFinite(lons[i])
                || Math.Abs(lat) > WebMercator.MaxLatitude || Math.Abs(lons[i]) > 180.0)
            {
                hasPrev = false; // don't connect across invalid samples
                continue;
            }
            var (x, y) = WebMercator.LatLonToGlobalPx(lat, lons[i], zoom);
            int gx = (int)Math.Round(x);
            int gy = (int)Math.Round(y);

            if (hasPrev && Math.Max(Math.Abs(gx - px), Math.Abs(gy - py)) <= MaxSegmentPx)
                PaintSegment(grid, painted, px, py, gx, gy);
            else
                PaintOnce(grid, painted, gx, gy);

            px = gx;
            py = gy;
            hasPrev = true;
        }
    }

    /// <summary>Bresenham line, both endpoints inclusive (dedup absorbs the shared endpoint).</summary>
    private static void PaintSegment(SparseTileGrid grid, HashSet<long> painted, int x0, int y0, int x1, int y1)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        while (true)
        {
            PaintOnce(grid, painted, x0, y0);
            if (x0 == x1 && y0 == y1) return;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    private static void PaintOnce(SparseTileGrid grid, HashSet<long> painted, int gx, int gy)
    {
        if (painted.Add(((long)gx << 32) | (uint)gy))
            grid.PaintPoint(gx, gy);
    }
}
