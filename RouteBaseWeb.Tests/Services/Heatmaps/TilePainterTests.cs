using RouteBaseWeb.Services.Heatmaps;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Heatmaps;

public class TilePainterTests
{
    private const int Zoom = 14;

    // Pixels near the equator so the projection round-trip is exact and lat stays valid.
    private const int BasePx = 2_000_000;

    /// <summary>Build lat/lon arrays whose projections land exactly on the given global pixels.</summary>
    private static (double[] Lats, double[] Lons) FromPixels(params (int X, int Y)[] px)
    {
        var lats = new double[px.Length];
        var lons = new double[px.Length];
        for (int i = 0; i < px.Length; i++)
            (lats[i], lons[i]) = WebMercator.GlobalPxToLatLon(px[i].X, px[i].Y, Zoom);
        return (lats, lons);
    }

    private static (long Total, int NonZero) CountPixels(SparseTileGrid grid)
    {
        long total = 0;
        int nonZero = 0;
        foreach (var (tx, ty) in grid.OccupiedTiles())
        {
            foreach (ushort v in grid.GetTile(tx, ty)!)
            {
                total += v;
                if (v > 0) nonZero++;
            }
        }
        return (total, nonZero);
    }

    [Fact]
    public void ConsecutivePoints_AreConnectedWithALine()
    {
        var grid = new SparseTileGrid();
        var (lats, lons) = FromPixels((BasePx, BasePx), (BasePx + 50, BasePx + 20));

        TilePainter.PaintTrack(grid, lats, lons, Zoom);

        // Bresenham paints Chebyshev distance + 1 pixels, each exactly once.
        var (total, nonZero) = CountPixels(grid);
        Assert.Equal(51, nonZero);
        Assert.Equal(51, total);
    }

    [Fact]
    public void RepeatedPixel_CountsOncePerTrack()
    {
        var grid = new SparseTileGrid();
        var (lats, lons) = FromPixels(
            (BasePx, BasePx), (BasePx, BasePx), (BasePx, BasePx), (BasePx, BasePx));

        TilePainter.PaintTrack(grid, lats, lons, Zoom);

        var (total, nonZero) = CountPixels(grid);
        Assert.Equal(1, nonZero);
        Assert.Equal(1, total);
    }

    [Fact]
    public void SeparateTracks_AccumulatePasses()
    {
        var grid = new SparseTileGrid();
        var (lats, lons) = FromPixels((BasePx, BasePx));

        TilePainter.PaintTrack(grid, lats, lons, Zoom);
        TilePainter.PaintTrack(grid, lats, lons, Zoom);

        var (total, nonZero) = CountPixels(grid);
        Assert.Equal(1, nonZero);
        Assert.Equal(2, total);
    }

    [Fact]
    public void LargeGap_IsNotConnected()
    {
        var grid = new SparseTileGrid();
        var (lats, lons) = FromPixels(
            (BasePx, BasePx), (BasePx + TilePainter.MaxSegmentPx + 100, BasePx));

        TilePainter.PaintTrack(grid, lats, lons, Zoom);

        var (total, nonZero) = CountPixels(grid);
        Assert.Equal(2, nonZero);
        Assert.Equal(2, total);
    }

    [Fact]
    public void InvalidSample_BreaksTheLine()
    {
        var (lats, lons) = FromPixels((BasePx, BasePx), (BasePx + 50, BasePx));
        double[] latsWithNaN = [lats[0], double.NaN, lats[1]];
        double[] lonsWithNaN = [lons[0], double.NaN, lons[1]];

        var grid = new SparseTileGrid();
        TilePainter.PaintTrack(grid, latsWithNaN, lonsWithNaN, Zoom);

        // NaN severs the segment: only the two endpoints are painted.
        var (total, nonZero) = CountPixels(grid);
        Assert.Equal(2, nonZero);
        Assert.Equal(2, total);
    }

    [Fact]
    public void PaintTrack_SkipsNonFiniteAndOutOfRangeLongitude()
    {
        // HM-01: an Infinity/out-of-range lon must not cast to a garbage pixel at an absurd tile.
        var grid = new SparseTileGrid();
        TilePainter.PaintTrack(grid,
            new[] { 50.0, 50.0, 50.0, 50.0 },
            new[] { double.PositiveInfinity, 200.0, -200.0, double.NaN }, Zoom);

        Assert.Equal(0, grid.OccupiedTileCount);
    }

    [Fact]
    public void PaintTrack_DoesNotConnectAcrossInvalidLongitude()
    {
        // 0.01° lon ≈ 116 px at z14 — well under MaxSegmentPx, so a connected pair would paint a line.
        var grid = new SparseTileGrid();
        TilePainter.PaintTrack(grid,
            new[] { 50.0, 50.0, 50.0 },
            new[] { 8.0, double.PositiveInfinity, 8.01 }, Zoom);

        var (total, nonZero) = CountPixels(grid);
        Assert.Equal(2, nonZero);
        Assert.Equal(2, total);
    }
}
