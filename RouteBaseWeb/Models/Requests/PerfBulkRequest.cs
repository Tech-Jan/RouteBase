namespace RouteBaseWeb.Models.Requests;

/// <summary>
/// Request body for the chunked performance-data endpoint: the GPSinSegment ids of the
/// efforts whose chart data this chunk needs.
/// </summary>
public class PerfBulkRequest
{
    public List<int> EffortIds { get; set; } = new();

    /// <summary>
    /// Per-effort point budget for server-side stride downsampling. 0 (default, or an absent field
    /// from old clients) means full resolution — today's behavior. The controller clamps positive
    /// values into a sane range before use.
    /// </summary>
    public int MaxPointsPerEffort { get; set; } = 0;
}
