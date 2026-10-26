namespace RouteBaseWeb.Models;

/// <summary>
/// A single day's activity totals for one raw activity type (per-day increments, not running totals).
/// One fetch serves both chart modes: the client sums across types for the aggregate line, or keeps them
/// split for the per-type multiline.
/// </summary>
public class CumulativeTypePoint
{
    /// <summary>Local calendar date the activities started on — a wall-clock string
    /// ("yyyy-MM-ddT00:00:00", FE-12); statistics-chart-store.ts slice-parses exactly this shape.</summary>
    public string Date { get; set; } = "";
    /// <summary>Raw activity type. Empty string for activities with no type.</summary>
    public string Type { get; set; } = "";
    /// <summary>Sum of distance for the day/type. Unit: kilometres.</summary>
    public double DistanceKm { get; set; }
    /// <summary>Sum of ascent for the day/type. Unit: metres.</summary>
    public double ElevationM { get; set; }
    /// <summary>Sum of moving time for the day/type. Unit: minutes.</summary>
    public double MovingTimeMin { get; set; }
    /// <summary>Number of activities on the day/type.</summary>
    public int Count { get; set; }
}
