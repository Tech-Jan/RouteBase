using RouteBaseWeb.Services.Elevation;
using RouteBaseWeb.Services.Heatmaps;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Elevation;

/// <summary>
/// Pins <see cref="TerrariumTileMath"/>: the pixel-center stencil for a known coordinate, tile/pixel
/// mapping with antimeridian wrap + pole clamp, and the bilinear value at the Jungfrau summit sampled
/// through the real fixture tile (decoder → math end-to-end).
/// </summary>
public class TerrariumTileMathTests
{
    // z13 world size in pixels: (1 << 13) tiles × 256 px.
    private const int WorldPx = 8192 * 256;

    // Jungfrau summit — the fixture tile (z13 x4277 y2896) covers this coordinate.
    private const double JungfrauLat = 46.5367;
    private const double JungfrauLon = 7.9625;

    private static float[] LoadFixtureTile() =>
        TerrariumTileDecoder.DecodeElevations(
            File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "terrarium_13_4277_2896.png")));

    [Fact]
    public void ComputeStencil_JungfrauSummit_MatchesExpectedOriginAndFractions()
    {
        var s = TerrariumTileMath.ComputeStencil(JungfrauLat, JungfrauLon);

        Assert.Equal(1094960, s.X0);
        Assert.Equal(741563, s.Y0);
        Assert.Equal(0.424444, s.Tx, 5);
        Assert.Equal(0.203717, s.Ty, 5);

        // Stencil origin maps to tile (4277, 2896) local pixel (48, 187) — the fixture tile.
        var (tileX, tileY, localX, localY) = TerrariumTileMath.PixelToTile(s.X0, s.Y0);
        Assert.Equal((4277, 2896, 48, 187), (tileX, tileY, localX, localY));
    }

    [Fact]
    public void PixelToTile_WrapsXAcrossAntimeridian()
    {
        // One pixel past the east edge wraps to the world's western origin.
        Assert.Equal((0, 0, 0, 0), TerrariumTileMath.PixelToTile(WorldPx, 0));
        // One pixel before the west edge wraps to the eastern-most tile.
        Assert.Equal((8191, 0, 255, 0), TerrariumTileMath.PixelToTile(-1, 0));
    }

    [Fact]
    public void PixelToTile_ClampsYAtPoles()
    {
        Assert.Equal(0, TerrariumTileMath.PixelToTile(0, -5).TileY);
        Assert.Equal(8191, TerrariumTileMath.PixelToTile(0, WorldPx + 5).TileY);
        Assert.Equal(255, TerrariumTileMath.PixelToTile(0, WorldPx + 5).LocalY);
    }

    [Fact]
    public void ComputeStencil_ClampsLatToMercatorLimit()
    {
        var beyond = TerrariumTileMath.ComputeStencil(89.0, 0.0);
        var atLimit = TerrariumTileMath.ComputeStencil(WebMercator.MaxLatitude, 0.0);
        Assert.Equal(atLimit.Y0, beyond.Y0);
    }

    [Fact]
    public void SampleBilinear_JungfrauSummit_MatchesFixtureValue()
    {
        var tile = LoadFixtureTile();
        float[]? GetTile((int TileX, int TileY) t) => t == (4277, 2896) ? tile : null;

        var elevation = TerrariumTileMath.SampleBilinear(JungfrauLat, JungfrauLon, GetTile);

        Assert.NotNull(elevation);
        Assert.Equal(3993.05, elevation!.Value, 2);
    }

    [Fact]
    public void SampleBilinear_MissingTile_ReturnsNull()
    {
        var elevation = TerrariumTileMath.SampleBilinear(JungfrauLat, JungfrauLon, _ => null);
        Assert.Null(elevation);
    }
}
