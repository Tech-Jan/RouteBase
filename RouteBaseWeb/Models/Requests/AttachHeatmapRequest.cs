namespace RouteBaseWeb.Models.Requests;

/// <summary>
/// Payload for attaching/regenerating a heatmap on an existing shapes segment. The segment's efforts
/// already define which tracks and spans are painted; <see cref="ActivityTypes"/> narrows the tiles by
/// activity type (empty/null → all types). Efforts themselves are never filtered.
/// </summary>
public class AttachHeatmapRequest
{
    /// <summary>Activity types to include in the tiles (e.g. ["Run","Ride"]). Empty/null → all types.</summary>
    public List<string>? ActivityTypes { get; set; }
}
