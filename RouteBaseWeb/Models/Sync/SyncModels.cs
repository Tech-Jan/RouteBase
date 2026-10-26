namespace RouteBaseWeb.Models.Sync;

/// <summary>
/// Provider-agnostic representation of an activity fetched from an external source.
/// </summary>
public class ExternalActivitySummary
{
    public long ExternalId { get; set; }
    public string ProviderId { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? ActivityType { get; set; }
    public DateTime? StartDateLocal { get; set; }
    public DateTime? StartDateUtc { get; set; }
    public double? StartLat { get; set; }
    public double? StartLng { get; set; }
    public double? EndLat { get; set; }
    public double? EndLng { get; set; }
    public double? Distance { get; set; }
    public double? TotalTime { get; set; }
    public double? MovingTime { get; set; }
    public double? ElapsedTime { get; set; }
    public double? ElevationGain { get; set; }
    public double? AverageSpeed { get; set; }
    public double? MaxSpeed { get; set; }
    public double? AverageHeartRate { get; set; }
    public double? MaxHeartRate { get; set; }
    public double? AverageCadence { get; set; }
    public double? MaxCadence { get; set; }
    public double? AveragePower { get; set; }
    public double? MaxPower { get; set; }
    public double? Calories { get; set; }
    public double? TotalDescent { get; set; }
    public float? TimezoneOffset { get; set; }
    public string? Description { get; set; }
    public string? Source { get; set; }
    public object? RawData { get; set; }
}

/// <summary>
/// Provider-agnostic representation of a single GPS point fetched from an external source.
/// </summary>
public class ExternalGpsPoint
{
    public double Lat { get; set; }
    public double Lng { get; set; }
    public double? Altitude { get; set; }
    public double? Distance { get; set; }
    public double? Speed { get; set; }
    public double? HeartRate { get; set; }
    public double? Cadence { get; set; }
    public double? Power { get; set; }
    public double? GradeSmooth { get; set; }
    public double? Pressure { get; set; }   // raw barometric hPa (live recording; null for imports)

    /// <summary>Dense server-assigned staging ordinal — the live-view catch-up cursor (LV-01). Null for
    /// parser-produced points (GPX/FIT/TCX), which never go through recording staging.</summary>
    public long? Seq { get; set; }

    public DateTime? TimestampUtc { get; set; }
}
