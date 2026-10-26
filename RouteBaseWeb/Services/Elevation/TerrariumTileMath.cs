using RouteBaseWeb.Services.Heatmaps;

namespace RouteBaseWeb.Services.Elevation;

/// <summary>
/// Pure lat/lon → terrarium tile + pixel math and bilinear sampling (no I/O — unit-testable without
/// mocks). z13 pixels are ~13 m at 47° N / ~19 m at the equator: finer than the ~30 m SRTM/EU-DEM
/// source, so z13 captures full source accuracy while z14+ only multiplies tile count. Reuses
/// <see cref="WebMercator.LatLonToGlobalPx"/> for the projection.
/// </summary>
public static class TerrariumTileMath
{
    public const int TerrariumZoom = 13;

    // US bucket only (the EU bucket 403s); PROCESS final-proof grep expects this URL in exactly one BE file.
    public const string TileUrlTemplate =
        "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{z}/{x}/{y}.png";

    private const int TileSize = WebMercator.TileSize;      // 256

    /// <summary>World width/height in pixels at <see cref="TerrariumZoom"/>.</summary>
    private static int WorldPx => (1 << TerrariumZoom) * TileSize;

    /// <summary>
    /// The 4-pixel bilinear stencil origin in global-pixel space, using the pixel-center convention
    /// (sample position = global px − 0.5). The 4 samples are (X0,Y0), (X0+1,Y0), (X0,Y0+1),
    /// (X0+1,Y0+1); each resolves to its own tile via <see cref="PixelToTile"/>, so a stencil near a
    /// tile edge naturally spans up to 4 tiles.
    /// </summary>
    public readonly record struct SampleStencil(int X0, int Y0, double Tx, double Ty);

    /// <summary>Compute the bilinear stencil for a coordinate (lat clamped to the Mercator limit).</summary>
    public static SampleStencil ComputeStencil(double lat, double lon)
    {
        double clampedLat = Math.Clamp(lat, -WebMercator.MaxLatitude, WebMercator.MaxLatitude);
        var (gx, gy) = WebMercator.LatLonToGlobalPx(clampedLat, lon, TerrariumZoom);
        double fx = gx - 0.5;
        double fy = gy - 0.5;
        int x0 = (int)Math.Floor(fx);
        int y0 = (int)Math.Floor(fy);
        return new SampleStencil(x0, y0, fx - x0, fy - y0);
    }

    /// <summary>
    /// Map a global-pixel coordinate to its tile + local pixel: x wraps modulo the world width
    /// (antimeridian), y clamps to [0, world−1] (poles have no tiles beyond the Mercator limit).
    /// </summary>
    public static (int TileX, int TileY, int LocalX, int LocalY) PixelToTile(int px, int py)
    {
        int world = WorldPx;
        int wx = ((px % world) + world) % world;
        int cy = Math.Clamp(py, 0, world - 1);
        return (wx >> 8, cy >> 8, wx & 255, cy & 255);
    }

    /// <summary>
    /// Bilinearly sample the elevation at (lat, lon). <paramref name="getTile"/> supplies a decoded
    /// row-major tile (float[65536]) for a tile coordinate, or null when that tile is missing.
    /// Returns null if any of the 4 stencil tiles is missing (caller decides the fallback).
    /// </summary>
    public static float? SampleBilinear(
        double lat, double lon, Func<(int TileX, int TileY), float[]?> getTile)
    {
        var s = ComputeStencil(lat, lon);
        float? e00 = SamplePixel(s.X0, s.Y0, getTile);
        float? e10 = SamplePixel(s.X0 + 1, s.Y0, getTile);
        float? e01 = SamplePixel(s.X0, s.Y0 + 1, getTile);
        float? e11 = SamplePixel(s.X0 + 1, s.Y0 + 1, getTile);
        if (e00 is null || e10 is null || e01 is null || e11 is null) return null;

        double top = Lerp(e00.Value, e10.Value, s.Tx);
        double bottom = Lerp(e01.Value, e11.Value, s.Tx);
        return (float)Lerp(top, bottom, s.Ty);
    }

    private static float? SamplePixel(int px, int py, Func<(int TileX, int TileY), float[]?> getTile)
    {
        var (tx, ty, lx, ly) = PixelToTile(px, py);
        var tile = getTile((tx, ty));
        return tile is null ? null : tile[ly * TileSize + lx];
    }

    private static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
