namespace RouteBaseWeb.Models;

/// <summary>
/// Detailed effort with GPS matching information.
/// </summary>
public class SegmentEffortDetailed
{
    public int GpsInSegmentId { get; set; }
    public int SegmentId { get; set; }
    public string? SegmentName { get; set; }
    public int ActivityId { get; set; }
    public string? ActivityName { get; set; }
    public DateTime? ActivityDate { get; set; }
    public string? ActivityType { get; set; }
    public int MatchPercentage { get; set; }
    public double Distance { get; set; }
    public double ElapsedTime { get; set; }
    public double? AverageSpeed { get; set; }
    public double? AverageHeartRate { get; set; }
    public double? AverageCadence { get; set; }
    public double? AveragePower { get; set; }
    public double Elevation { get; set; }
    public int GpsIdEntry { get; set; }
    public int GpsIdExit { get; set; }
    public double? OffsetEntry { get; set; }
    public double? OffsetExit { get; set; }

    // Cross-user fields — populated when effort belongs to a friend.
    public string? UserId { get; set; }
    public string? UserDisplayName { get; set; }
    public bool IsOwnEffort { get; set; }

    /// <summary>
    /// True when a durable <see cref="SegmentEffortExclusion"/> hides this effort (item 25). Excluded
    /// efforts still appear in the segment-stats grid (marked, with a Restore action) but are anti-joined
    /// out of totals/counts/charts/heatmap paint. Set via the efforts-list LEFT JOIN.
    /// </summary>
    public bool Excluded { get; set; }
}
