namespace RouteBaseWeb.Services.Heatmaps;

/// <summary>
/// Thrown when painting would exceed <see cref="SparseTileGrid.MaxOccupiedTiles"/>.
/// Caller reacts by dropping the max zoom one level and repainting.
/// </summary>
public class TileCapExceededException(int cap)
    : InvalidOperationException($"Heatmap exceeds the cap of {cap} occupied tiles.")
{
    public int Cap { get; } = cap;
}

/// <summary>
/// Sparse per-tile count accumulators at one zoom level. Memory scales with the
/// number of occupied tiles, not the data's bounding box. Counts saturate at
/// ushort max — visually irrelevant after log normalization.
/// </summary>
public sealed class SparseTileGrid(int maxOccupiedTiles = SparseTileGrid.DefaultMaxOccupiedTiles)
{
    // Each occupied tile holds a ushort[65536] = 128 KB. The cap bounds one generation's peak grid RAM
    // (128 MB at 1024 tiles); a segment that would exceed it degrades max-zoom by one and repaints
    // (HeatmapGenerationService.PaintWithCapFallbackAsync). Sized for a small-RAM host; generation is
    // single-threaded (OperationQueueWorker) so only one grid is ever resident.
    public const int DefaultMaxOccupiedTiles = 1024;
    private const int PixelsPerTile = WebMercator.TileSize * WebMercator.TileSize;

    private readonly Dictionary<(int Tx, int Ty), ushort[]> _tiles = new();

    public int MaxOccupiedTiles { get; } = maxOccupiedTiles;

    public int OccupiedTileCount => _tiles.Count;

    /// <summary>Snapshot of occupied tile coordinates (safe to remove tiles while iterating).</summary>
    public List<(int Tx, int Ty)> OccupiedTiles() => [.. _tiles.Keys];

    /// <summary>Row-major 256×256 count grid, or null if the tile is empty.</summary>
    public ushort[]? GetTile(int tx, int ty) => _tiles.GetValueOrDefault((tx, ty));

    public bool RemoveTile(int tx, int ty) => _tiles.Remove((tx, ty));

    /// <summary>Increment the count at global pixel (gx, gy), saturating at ushort max.</summary>
    public void PaintPoint(int gx, int gy)
    {
        int tx = WebMercator.FloorDiv(gx, WebMercator.TileSize);
        int ty = WebMercator.FloorDiv(gy, WebMercator.TileSize);
        int lx = gx - tx * WebMercator.TileSize;
        int ly = gy - ty * WebMercator.TileSize;
        var tile = GetOrCreateTile(tx, ty);
        int idx = ly * WebMercator.TileSize + lx;
        if (tile[idx] < ushort.MaxValue)
            tile[idx]++;
    }

    internal ushort[] GetOrCreateTile(int tx, int ty)
    {
        if (_tiles.TryGetValue((tx, ty), out var tile))
            return tile;
        if (_tiles.Count >= MaxOccupiedTiles)
            throw new TileCapExceededException(MaxOccupiedTiles);
        tile = new ushort[PixelsPerTile];
        _tiles[(tx, ty)] = tile;
        return tile;
    }
}
