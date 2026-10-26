namespace RouteBaseWeb.Models;

/// <summary>
/// One decoded GPS point. Persistence-free POCO since BlobStore: the GPS table is gone and point data lives
/// packed in Track_Blob (decoded via the codec / ITrackReader). Materialized by consumers that still want a
/// per-point object (Details, GPX export, split calc, normalization/smoothing, elevation). GpsId is the
/// track ordinal (0-based) post-migration.
/// </summary>
public class GpsData
{
    public int GpsId { get; set; }

    public int TrackId { get; set; }

    public DateTime? DateTimeUtc { get; set; }

    public double? GpsLat { get; set; }

    public double? GpsLong { get; set; }

    public double? Distance { get; set; }

    public double? Speed { get; set; }

    public double? Altitude { get; set; }

    public double? HeartRate { get; set; }

    public double? Cadence { get; set; }

    public double? Power { get; set; }

    public double? GradeSmooth { get; set; }

    /// <summary>Raw barometric pressure in hPa, captured by the phone when it has a barometer (null
    /// otherwise). Stored verbatim — the DEM elevation replace never touches it. Consumed by
    /// <c>Services.Elevation.ElevationFusion</c> via <c>GpsNormalizationService.SmoothAltitudes</c>,
    /// which fuses it with the DEM when a track's coverage clears 50 %.</summary>
    public double? Pressure { get; set; }
}
