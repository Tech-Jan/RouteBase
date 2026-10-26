namespace RouteBaseWeb.Services.Heatmaps;

/// <summary>
/// Builds the next-lower-zoom grid by combining 2×2 child tiles into parents
/// via 2×2 pixel sums. Pops each child after use so peak memory is roughly
/// halved during the downsample instead of holding both zooms fully.
/// </summary>
public static class TileDownsampler
{
    public static SparseTileGrid Downsample(SparseTileGrid childGrid)
    {
        const int size = WebMercator.TileSize;
        const int half = size / 2;
        var parentGrid = new SparseTileGrid(childGrid.MaxOccupiedTiles);

        foreach (var (cx, cy) in childGrid.OccupiedTiles())
        {
            var child = childGrid.GetTile(cx, cy)!;
            childGrid.RemoveTile(cx, cy);

            // Arithmetic shift floors for negatives; & 1 picks the quadrant.
            var parent = parentGrid.GetOrCreateTile(cx >> 1, cy >> 1);
            int qxLo = (cx & 1) * half;
            int qyLo = (cy & 1) * half;

            for (int py = 0; py < half; py++)
            {
                int srcRow0 = (2 * py) * size;
                int srcRow1 = srcRow0 + size;
                int dstRow = (qyLo + py) * size + qxLo;
                for (int px = 0; px < half; px++)
                {
                    int sx = 2 * px;
                    int sum = child[srcRow0 + sx] + child[srcRow0 + sx + 1]
                            + child[srcRow1 + sx] + child[srcRow1 + sx + 1];
                    parent[dstRow + px] = (ushort)Math.Min(sum, ushort.MaxValue);
                }
            }
        }
        return parentGrid;
    }
}
