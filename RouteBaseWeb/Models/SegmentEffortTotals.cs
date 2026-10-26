namespace RouteBaseWeb.Models;

/// <summary>
/// Aggregate totals across every effort on a segment — the sum of each effort's distance,
/// elevation gain, and elapsed time. Drives the "/ Total" readouts on the segment cards.
/// </summary>
public class SegmentEffortTotals
{
    public int EffortCount { get; set; }
    /// <summary>Sum of per-effort distances, in metres.</summary>
    public double TotalDistance { get; set; }
    /// <summary>Sum of per-effort elevation gain, in metres.</summary>
    public double TotalElevation { get; set; }
    /// <summary>Sum of per-effort elapsed time, in seconds.</summary>
    public double TotalTime { get; set; }
}
