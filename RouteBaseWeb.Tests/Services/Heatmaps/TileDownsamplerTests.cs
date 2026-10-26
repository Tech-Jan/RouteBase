using RouteBaseWeb.Services.Heatmaps;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Heatmaps;

public class TileDownsamplerTests
{
    [Fact]
    public void Downsample_SumsTwoByTwoBlocksIntoCorrectQuadrant()
    {
        var child = new SparseTileGrid();
        // Tile (2, 3) → parent (1, 1), quadrant x=0, y=1 (offset 0, 128).
        var tile = child.GetOrCreateTile(2, 3);
        tile[0 * 256 + 0] = 5;
        tile[0 * 256 + 1] = 3;
        tile[1 * 256 + 0] = 2;
        tile[1 * 256 + 1] = 7;

        var parent = TileDownsampler.Downsample(child);

        var pTile = parent.GetTile(1, 1);
        Assert.NotNull(pTile);
        Assert.Equal(17, pTile[128 * 256 + 0]);
        Assert.Equal(1, parent.OccupiedTileCount);
    }

    [Fact]
    public void Downsample_PopsChildrenDuringProcessing()
    {
        var child = new SparseTileGrid();
        child.PaintPoint(10, 10);
        child.PaintPoint(600, 600);

        TileDownsampler.Downsample(child);

        Assert.Equal(0, child.OccupiedTileCount);
    }

    [Fact]
    public void Downsample_TotalMassIsConserved()
    {
        var child = new SparseTileGrid();
        var rng = new Random(42);
        for (int i = 0; i < 500; i++)
            child.PaintPoint(rng.Next(0, 1024), rng.Next(0, 1024));

        var parent = TileDownsampler.Downsample(child);

        long total = 0;
        foreach (var (tx, ty) in parent.OccupiedTiles())
            foreach (ushort v in parent.GetTile(tx, ty)!)
                total += v;
        Assert.Equal(500, total);
    }

    [Fact]
    public void Downsample_SaturatesAtUShortMax()
    {
        var child = new SparseTileGrid();
        var tile = child.GetOrCreateTile(0, 0);
        tile[0] = 60000;
        tile[1] = 60000;
        tile[256] = 60000;
        tile[257] = 60000;

        var parent = TileDownsampler.Downsample(child);

        Assert.Equal(ushort.MaxValue, parent.GetTile(0, 0)![0]);
    }

    [Fact]
    public void Downsample_NegativeTileCoords_FloorToCorrectParent()
    {
        var child = new SparseTileGrid();
        child.GetOrCreateTile(-1, -1)[0] = 1;

        var parent = TileDownsampler.Downsample(child);

        // -1 >> 1 == -1; quadrant (-1 & 1) == 1 → offset (128, 128).
        var pTile = parent.GetTile(-1, -1);
        Assert.NotNull(pTile);
        Assert.Equal(1, pTile[128 * 256 + 128]);
    }
}
