namespace RouteBaseWeb.Services.Heatmaps;

/// <summary>
/// Web-Mercator projection and OSM tile math (NW origin; at zoom z the world
/// is 2^z × 2^z tiles of <see cref="TileSize"/> pixels each).
/// </summary>
public static class WebMercator
{
    public const int TileSize = 256;

    /// <summary>Web-Mercator latitude limit — the projection diverges beyond it.</summary>
    public const double MaxLatitude = 85.05112878;

    /// <summary>Lat/lon → global pixel coordinates at zoom z.</summary>
    public static (double X, double Y) LatLonToGlobalPx(double lat, double lon, int zoom)
    {
        double n = (1L << zoom) * (double)TileSize;
        double x = (lon + 180.0) / 360.0 * n;
        double latRad = lat * Math.PI / 180.0;
        double y = (1.0 - Math.Log(Math.Tan(latRad) + 1.0 / Math.Cos(latRad)) / Math.PI) / 2.0 * n;
        return (x, y);
    }

    /// <summary>Inverse of <see cref="LatLonToGlobalPx"/>: global pixel → lat/lon at zoom z.</summary>
    public static (double Lat, double Lon) GlobalPxToLatLon(double x, double y, int zoom)
    {
        double n = (1L << zoom) * (double)TileSize;
        double lon = x / n * 360.0 - 180.0;
        double latRad = Math.Atan(Math.Sinh(Math.PI * (1.0 - 2.0 * y / n)));
        return (latRad * 180.0 / Math.PI, lon);
    }

    /// <summary>Floor division — tile index stays correct for negative pixel coords.</summary>
    public static int FloorDiv(int value, int divisor)
    {
        int q = value / divisor;
        return (value ^ divisor) < 0 && q * divisor != value ? q - 1 : q;
    }
}
