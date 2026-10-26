namespace RouteBaseWeb.Models.Requests;

public class GpsDataRequest
{
    public List<int> ActivityIds { get; set; } = new();

    /// <summary>
    /// Maximum GPS points per track. Lower values load faster (use for zoomed-out overview),
    /// higher values give better detail (use when zoomed in). Default: 100.
    /// </summary>
    public int MaxPointsPerTrack { get; set; } = 100;
}
