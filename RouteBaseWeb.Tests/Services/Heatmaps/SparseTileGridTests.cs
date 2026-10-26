using RouteBaseWeb.Services.Heatmaps;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Heatmaps;

public class SparseTileGridTests
{
    [Fact]
    public void PaintPoint_IncrementsCorrectPixelInCorrectTile()
    {
        var grid = new SparseTileGrid();
        grid.PaintPoint(300, 40); // tile (1, 0), local (44, 40)

        var tile = grid.GetTile(1, 0);
        Assert.NotNull(tile);
        Assert.Equal(1, tile[40 * 256 + 44]);
        Assert.Equal(1, grid.OccupiedTileCount);
    }

    [Fact]
    public void PaintPoint_SamePixelTwice_CountsTwo()
    {
        var grid = new SparseTileGrid();
        grid.PaintPoint(10, 10);
        grid.PaintPoint(10, 10);
        Assert.Equal(2, grid.GetTile(0, 0)![10 * 256 + 10]);
    }

    [Fact]
    public void PaintPoint_BeyondCap_Throws()
    {
        var grid = new SparseTileGrid(maxOccupiedTiles: 2);
        grid.PaintPoint(0, 0);
        grid.PaintPoint(256, 0);
        var ex = Assert.Throws<TileCapExceededException>(() => grid.PaintPoint(512, 0));
        Assert.Equal(2, ex.Cap);
    }

    [Fact]
    public void PaintTrack_ProjectsAndDeduplicatesWithinOneTrack()
    {
        // Two samples on the same pixel count once — counts mean passes, not samples.
        var grid = new SparseTileGrid();
        double[] lats = [47.5, 47.5];
        double[] lons = [11.3, 11.3];
        TilePainter.PaintTrack(grid, lats, lons, 16);

        var (x, y) = WebMercator.LatLonToGlobalPx(47.5, 11.3, 16);
        int gx = (int)Math.Round(x);
        int gy = (int)Math.Round(y);
        var tile = grid.GetTile(gx / 256, gy / 256);
        Assert.NotNull(tile);
        Assert.Equal(1, tile[(gy % 256) * 256 + gx % 256]);
    }

    [Fact]
    public void PaintTrack_SkipsInvalidLatitudes()
    {
        var grid = new SparseTileGrid();
        TilePainter.PaintTrack(grid, [89.9, double.NaN], [11.0, 11.0], 16);
        Assert.Equal(0, grid.OccupiedTileCount);
    }
}
