namespace RouteBaseWeb.Services.Heatmaps;

/// <summary>One rendered tile ready for persistence: its {z}/{x}/{y} address plus PNG bytes.</summary>
public readonly record struct HeatmapTileData(int Z, int X, int Y, byte[] Png);

/// <summary>
/// Pyramid-level results: zoom range, the base-zoom raw count maximum (≈ max distinct passes
/// through one pixel), and the geographic bounds of the occupied data (south/west/north/east).
/// </summary>
public sealed record HeatmapMetadata(
    int MinZoom, int MaxZoom, double CountMax,
    double South, double West, double North, double East);

/// <summary>
/// Orchestrates the zoom pyramid from an already-painted z_max <see cref="SparseTileGrid"/>:
/// for each zoom z_max→z_min it scans the raw pass-count max, blurs+normalizes+colour-maps
/// each tile to a PNG, flushes that zoom's tiles, then 2×2-sum downsamples to the parent zoom
/// (popping children so peak memory stays near one zoom's worth of tiles).
/// Normalization is anchored to the analytic single-pass value: norm = log(1 + passes) /
/// log(1 + maxPasses), so one pass always maps to a clearly visible color regardless of the
/// hottest pixel. V1 renders only the "count_log" layer.
/// </summary>
public sealed class HeatmapPyramidBuilder(double sigma = GaussianBlur.DefaultSigma)
{
    /// <summary>Auto min-zoom target: stop zooming out once the data spans fewer than this many px.</summary>
    public const int MinZoomTargetPx = 640;

    /// <summary>Floor for the normalization pass range — keeps single-track renders off pure white.</summary>
    public const double MinPassRange = 4.0;

    private const int TileSize = WebMercator.TileSize;
    private const int PixelsPerTile = TileSize * TileSize;

    private readonly double _sigma = sigma;

    /// <summary>Tiles per flush call — bounds peak PNG memory to one batch (instead of a whole
    /// zoom's worth, ~100-200 MB dense) and keeps each insert transaction short so concurrent
    /// tile reads aren't starved during generation.</summary>
    public const int FlushBatchTiles = 256;

    /// <summary>
    /// Build the pyramid. <paramref name="flushZoomAsync"/> is awaited with rendered-tile batches
    /// (≤ <see cref="FlushBatchTiles"/> per call, possibly several calls per zoom) so callers
    /// persist incrementally and memory stays bounded; <paramref name="onZoom"/> fires at the
    /// start of each zoom for progress reporting.
    /// </summary>
    public async Task<HeatmapMetadata> BuildAsync(
        SparseTileGrid baseGrid,
        int maxZoom,
        Func<int, IReadOnlyList<HeatmapTileData>, Task> flushZoomAsync,
        Action<int>? onZoom = null,
        CancellationToken ct = default)
    {
        if (baseGrid.OccupiedTileCount == 0)
            throw new InvalidOperationException("No occupied tiles — check input tracks.");

        var (south, west, north, east) = OccupiedBoundsLatLon(baseGrid, maxZoom);
        int minZoom = AutoMinZoom(baseGrid, maxZoom);
        double baseCountMax = 0;

        var grid = baseGrid;
        for (int z = maxZoom; z >= minZoom; z--)
        {
            ct.ThrowIfCancellationRequested();
            onZoom?.Invoke(z);
            double rawMax = ComputeRawMax(grid, ct);
            if (z == maxZoom) baseCountMax = rawMax;

            await RenderAndFlushTilesAsync(grid, z, rawMax, maxZoom, flushZoomAsync, ct);

            if (z > minZoom)
                grid = TileDownsampler.Downsample(grid);
        }

        return new HeatmapMetadata(minZoom, maxZoom, baseCountMax, south, west, north, east);
    }

    /// <summary>Raw (pre-blur) count maximum across all occupied tiles — max passes at this zoom.</summary>
    private static double ComputeRawMax(SparseTileGrid grid, CancellationToken ct)
    {
        ushort max = 0;
        foreach (var (tx, ty) in grid.OccupiedTiles())
        {
            ct.ThrowIfCancellationRequested();
            foreach (ushort v in grid.GetTile(tx, ty)!)
                if (v > max) max = v;
        }
        return Math.Max((int)max, 1);
    }

    /// <summary>Blur, pass-normalize, colour-map and PNG-encode every occupied tile (blank ones
    /// dropped), flushing to <paramref name="flushAsync"/> every <see cref="FlushBatchTiles"/>.</summary>
    private async Task RenderAndFlushTilesAsync(
        SparseTileGrid grid, int zoom, double rawMax, int maxZoom,
        Func<int, IReadOnlyList<HeatmapTileData>, Task> flushAsync, CancellationToken ct)
    {
        // One pass paints ~1 count/px at z_max; each 2×2-sum downsample doubles that.
        double singlePassRaw = Math.Pow(2, maxZoom - zoom);
        double vRef = GaussianBlur.LinePeak(_sigma) * singlePassRaw;
        double logPassMax = Math.Log(1.0 + Math.Max(rawMax / singlePassRaw, MinPassRange));
        var batch = new List<HeatmapTileData>(FlushBatchTiles);
        var rgba = new byte[PixelsPerTile * 4];
        // One LOH-sized scratch set per zoom instead of ~800 KB of fresh arrays per tile
        // (generation is single-threaded on the operation queue).
        var scratch = new GaussianBlur.BlurScratch(_sigma);

        foreach (var (tx, ty) in grid.OccupiedTiles())
        {
            ct.ThrowIfCancellationRequested();
            var blurred = GaussianBlur.BlurTile(grid, tx, ty, _sigma, scratch);

            byte maxAlpha = 0;
            for (int p = 0; p < PixelsPerTile; p++)
            {
                int o = p * 4;
                // Zero fast path: tracks are ~1-px blurred lines, so most pixels of an occupied tile are
                // exactly 0f — norm would be Log(1)=0 and Lookup(0) is (0,0,0,0). Write the zeros directly
                // (rgba is reused across tiles, so the write itself is required); skip the Log + LUT.
                if (blurred[p] == 0f)
                {
                    rgba[o] = 0; rgba[o + 1] = 0; rgba[o + 2] = 0; rgba[o + 3] = 0;
                    continue;
                }
                double norm = Math.Min(Math.Log(1.0 + blurred[p] / vRef) / logPassMax, 1.0);
                var (r, g, b, a) = CountColorMap.Lookup(norm);
                rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = a;
                if (a > maxAlpha) maxAlpha = a;
            }
            if (maxAlpha == 0) continue; // fully transparent — nothing to show

            batch.Add(new HeatmapTileData(zoom, tx, ty, PngEncoder.EncodeRgba(rgba, TileSize, TileSize)));
            if (batch.Count >= FlushBatchTiles)
            {
                await flushAsync(zoom, batch); // flush completes before we reuse the list
                batch.Clear();
            }
        }

        if (batch.Count > 0)
            await flushAsync(zoom, batch);
    }

    /// <summary>([south, west, north, east]) of the occupied-tile bounding box at <paramref name="zoom"/>.</summary>
    private static (double South, double West, double North, double East) OccupiedBoundsLatLon(
        SparseTileGrid grid, int zoom)
    {
        int txMin = int.MaxValue, txMax = int.MinValue, tyMin = int.MaxValue, tyMax = int.MinValue;
        foreach (var (tx, ty) in grid.OccupiedTiles())
        {
            if (tx < txMin) txMin = tx;
            if (tx > txMax) txMax = tx;
            if (ty < tyMin) tyMin = ty;
            if (ty > tyMax) tyMax = ty;
        }

        var (latNw, lonNw) = WebMercator.GlobalPxToLatLon(txMin * TileSize, tyMin * TileSize, zoom);
        var (latSe, lonSe) = WebMercator.GlobalPxToLatLon((txMax + 1) * TileSize, (tyMax + 1) * TileSize, zoom);
        return (latSe, lonNw, latNw, lonSe);
    }

    /// <summary>Lowest zoom where the data still fills ≥ <see cref="MinZoomTargetPx"/> px of viewport.</summary>
    private static int AutoMinZoom(SparseTileGrid grid, int maxZoom)
    {
        int txMin = int.MaxValue, txMax = int.MinValue, tyMin = int.MaxValue, tyMax = int.MinValue;
        foreach (var (tx, ty) in grid.OccupiedTiles())
        {
            if (tx < txMin) txMin = tx;
            if (tx > txMax) txMax = tx;
            if (ty < tyMin) tyMin = ty;
            if (ty > tyMax) tyMax = ty;
        }

        int spanTiles = Math.Max(txMax - txMin + 1, tyMax - tyMin + 1);
        int spanPx = spanTiles * TileSize;
        if (spanPx <= MinZoomTargetPx) return maxZoom;
        return Math.Max(0, (int)Math.Ceiling(maxZoom - Math.Log2((double)spanPx / MinZoomTargetPx)));
    }
}
