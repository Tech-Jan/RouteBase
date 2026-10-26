namespace RouteBaseWeb.Models.Requests;

public class BulkTrackRequest
{
    public List<int> TrackIds { get; set; } = new();

    /// <summary>
    /// Maximum GPS points per track. 0 or negative = the 2000-point safety cap (NOT unlimited —
    /// SA-05: a bulk full-res decode of up to 1000 tracks is a DoS vector). Values above 2000 clamp.
    /// Defaults to 200 — enough for smooth map overlays without over-fetching.
    /// </summary>
    public int MaxPointsPerTrack { get; set; } = 200;
}
