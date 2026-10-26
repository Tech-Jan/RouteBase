namespace RouteBaseWeb.Services;

/// <summary>
/// Computed statistics returned by <see cref="IGpsNormalizationService.ComputeStats"/>.
/// All distance/altitude values are in metres; all time values are in seconds; speed in m/s.
/// </summary>
public sealed record GpsNormalizationStats
{
    public DateTime? StartUtc { get; init; }
    public double? StartLat { get; init; }
    public double? StartLng { get; init; }
    public double? EndLat { get; init; }
    public double? EndLng { get; init; }
    public double TotalDistanceMeters { get; init; }
    public double TotalAscentMeters { get; init; }
    public double TotalDescentMeters { get; init; }
    public double TimeTotalSeconds { get; init; }
    public double TimeMovingSeconds { get; init; }
    public double SpeedAvgMs { get; init; }
    public double SpeedMaxMs { get; init; }
}
