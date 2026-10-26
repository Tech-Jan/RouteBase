namespace RouteBaseWeb.Models;

/// <summary>
/// Lightweight projection of Activity for map-overlay responses.
/// Contains only the 6 columns read by the gps-data endpoint — avoids
/// materializing all 28 Activity columns for a map-overlay request.
/// </summary>
public sealed class ActivityMapSlice
{
    public int       TrackId             { get; init; }
    public string?   TrackName           { get; init; }
    public string?   ActivityType        { get; init; }
    public DateTime? StartDateTimeLocal  { get; init; }
    public double?   TotalDistance       { get; init; }
    public double?   TotalAscent         { get; init; }
}
