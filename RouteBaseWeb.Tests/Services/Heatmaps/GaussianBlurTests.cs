using RouteBaseWeb.Services.Heatmaps;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Heatmaps;

public class GaussianBlurTests
{
    /// <summary>Same normalized kernel formula as the implementation (σ=2 → radius 8).</summary>
    private static double[] Kernel(double sigma, out int radius)
    {
        radius = (int)(4.0 * sigma + 0.5);
        var k = new double[2 * radius + 1];
        double sum = 0;
        for (int i = -radius; i <= radius; i++)
        {
            k[i + radius] = Math.Exp(-(i * (double)i) / (2 * sigma * sigma));
            sum += k[i + radius];
        }
        for (int i = 0; i < k.Length; i++)
            k[i] /= sum;
        return k;
    }

    [Fact]
    public void SinglePointInTileCenter_MassIsConserved()
    {
        var grid = new SparseTileGrid();
        grid.PaintPoint(128, 128);

        var blurred = GaussianBlur.BlurTile(grid, 0, 0);

        double sum = 0;
        foreach (float v in blurred)
            sum += v;
        Assert.Equal(1.0, sum, 4);
    }

    [Fact]
    public void SinglePointNearBorder_MassSplitsAcrossBothTiles()
    {
        var grid = new SparseTileGrid();
        grid.PaintPoint(255, 128); // last column of tile (0,0)

        var left = GaussianBlur.BlurTile(grid, 0, 0);
        var right = GaussianBlur.BlurTile(grid, 1, 0);

        double sum = 0;
        foreach (float v in left) sum += v;
        foreach (float v in right) sum += v;
        Assert.Equal(1.0, sum, 4);
    }

    [Fact]
    public void SinglePointNearBorder_BothTilesMatchAnalyticKernel()
    {
        var grid = new SparseTileGrid();
        grid.PaintPoint(255, 128);
        var k = Kernel(GaussianBlur.DefaultSigma, out int radius);

        var left = GaussianBlur.BlurTile(grid, 0, 0);
        var right = GaussianBlur.BlurTile(grid, 1, 0);

        // Single impulse → blurred[y, x] = k[y - 128] * k[x - 255], global px.
        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                double expected = k[dy + radius] * k[dx + radius];
                int gx = 255 + dx;
                int gy = 128 + dy;
                float actual = gx < 256
                    ? left[gy * 256 + gx]
                    : right[gy * 256 + (gx - 256)];
                Assert.Equal(expected, actual, 6);
            }
        }
    }

    [Fact]
    public void SeamIsContinuous_NoJumpAtTileBoundary()
    {
        // Uniform horizontal line crossing the border: blurred values on both
        // sides of the seam must be equal (the line is translation-invariant in x).
        var grid = new SparseTileGrid();
        for (int gx = 200; gx < 312; gx++)
            grid.PaintPoint(gx, 128);

        var left = GaussianBlur.BlurTile(grid, 0, 0);
        var right = GaussianBlur.BlurTile(grid, 1, 0);

        for (int y = 120; y <= 136; y++)
            Assert.Equal(left[y * 256 + 255], right[y * 256 + 0], 5);
    }

    [Fact]
    public void LinePeak_MatchesBlurredLineCenter()
    {
        // A long straight 1px line of 1s blurs to a cross-profile whose peak is
        // the 1D kernel's center weight — the normalization anchor must match.
        var grid = new SparseTileGrid();
        for (int gx = -256; gx < 512; gx++)
            grid.PaintPoint(gx, 128);

        var blurred = GaussianBlur.BlurTile(grid, 0, 0);

        double expected = GaussianBlur.LinePeak(GaussianBlur.DefaultSigma);
        Assert.Equal(expected, blurred[128 * 256 + 128], 5);
    }

    [Fact]
    public void EmptyNeighbors_BlurStillWorks()
    {
        var grid = new SparseTileGrid();
        grid.PaintPoint(0, 0); // tile (0,0) corner, all neighbors empty
        var blurred = GaussianBlur.BlurTile(grid, 0, 0);
        Assert.True(blurred[0] > 0);
    }
}
