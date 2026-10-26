using RouteBaseWeb.Services.Heatmaps;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Heatmaps;

public class HeatmapPyramidBuilderTests
{
    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Paint a synthetic track (a lat/lon line of points) into a grid at the given zoom.</summary>
    private static SparseTileGrid PaintLine(double lat0, double lon0, double lat1, double lon1, int n, int zoom)
    {
        var grid = new SparseTileGrid();
        var lats = new double[n];
        var lons = new double[n];
        for (int i = 0; i < n; i++)
        {
            double t = i / (double)(n - 1);
            lats[i] = lat0 + (lat1 - lat0) * t;
            lons[i] = lon0 + (lon1 - lon0) * t;
        }
        TilePainter.PaintTrack(grid, lats, lons, zoom);
        return grid;
    }

    private static async Task<(HeatmapMetadata Meta, Dictionary<int, List<HeatmapTileData>> ByZoom)> RunAsync(
        SparseTileGrid grid, int maxZoom)
    {
        var byZoom = new Dictionary<int, List<HeatmapTileData>>();
        var builder = new HeatmapPyramidBuilder();
        var meta = await builder.BuildAsync(grid, maxZoom, (z, tiles) =>
        {
            byZoom[z] = tiles.ToList();
            return Task.CompletedTask;
        });
        return (meta, byZoom);
    }

    [Fact]
    public async Task Build_TightCluster_SingleZoom_ProducesValidPngTiles()
    {
        // A cluster spanning far less than one tile → data fits in < MinZoomTargetPx, so minZoom == maxZoom.
        var grid = PaintLine(47.3760, 8.5400, 47.3765, 8.5405, 200, zoom: 14);

        var (meta, byZoom) = await RunAsync(grid, maxZoom: 14);

        Assert.Equal(14, meta.MaxZoom);
        Assert.Equal(14, meta.MinZoom);
        Assert.True(meta.CountMax > 0);

        var tiles = byZoom[14];
        Assert.NotEmpty(tiles);
        foreach (var t in tiles)
        {
            Assert.Equal(14, t.Z);
            Assert.True(t.Png.Length > PngSignature.Length);
            Assert.Equal(PngSignature, t.Png.Take(PngSignature.Length));
        }
    }

    [Fact]
    public async Task Build_WideSpread_MultiZoom_DownsamplesToFewerTiles()
    {
        // ~0.1° of longitude at z=14 spans several tiles → minZoom drops below maxZoom.
        var grid = PaintLine(47.30, 8.50, 47.32, 8.60, 2000, zoom: 14);
        int occupiedAtMax = grid.OccupiedTileCount;
        Assert.True(occupiedAtMax >= 3, $"expected a multi-tile spread, got {occupiedAtMax}");

        var (meta, byZoom) = await RunAsync(grid, maxZoom: 14);

        Assert.Equal(14, meta.MaxZoom);
        Assert.True(meta.MinZoom < meta.MaxZoom);

        // Every zoom in [min, max] is flushed, and lower zooms hold no more tiles than higher ones.
        for (int z = meta.MinZoom; z <= meta.MaxZoom; z++)
            Assert.True(byZoom.ContainsKey(z), $"zoom {z} not flushed");
        Assert.True(byZoom[meta.MinZoom].Count <= byZoom[meta.MaxZoom].Count);
    }

    [Fact]
    public async Task Build_BoundsContainInputPoints()
    {
        const double south = 47.30, west = 8.50, north = 47.32, east = 8.60;
        var grid = PaintLine(south, west, north, east, 2000, zoom: 14);

        var (meta, _) = await RunAsync(grid, maxZoom: 14);

        // Tile-aligned bounds must fully enclose the painted extent.
        Assert.True(meta.South <= south, $"south {meta.South} > {south}");
        Assert.True(meta.North >= north, $"north {meta.North} < {north}");
        Assert.True(meta.West <= west, $"west {meta.West} > {west}");
        Assert.True(meta.East >= east, $"east {meta.East} < {east}");
    }

    [Fact]
    public async Task Build_SinglePass_CountMaxIsOne()
    {
        // One deduped track → raw pass count is exactly 1 everywhere it touches.
        var grid = PaintLine(47.3760, 8.5400, 47.3765, 8.5405, 200, zoom: 14);

        var (meta, _) = await RunAsync(grid, maxZoom: 14);

        Assert.Equal(1.0, meta.CountMax);
    }

    [Fact]
    public async Task Build_EmptyGrid_Throws()
    {
        var builder = new HeatmapPyramidBuilder();
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.BuildAsync(new SparseTileGrid(), 14, (_, _) => Task.CompletedTask));
    }
}
