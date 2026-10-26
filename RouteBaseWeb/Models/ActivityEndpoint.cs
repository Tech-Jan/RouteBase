namespace RouteBaseWeb.Models;

/// <summary>
/// Start/end GPS coordinates of an activity, read directly from the Activity table
/// (no GPS join). Used to compute an initial map viewport before any track GPS is
/// loaded, so the camera can be positioned once up front instead of jumping after load.
/// Any coordinate may be null for activities without recorded GPS.
/// Serialized as-is by POST /api/activities/bounds — mirrors TS <c>ActivityEndpointDto</c>
/// (overview/activities-gps-loader.ts); adding a property here changes that wire contract.
/// </summary>
public sealed class ActivityEndpoint
{
    public double? StartLat { get; init; }
    public double? StartLng { get; init; }
    public double? EndLat   { get; init; }
    public double? EndLng   { get; init; }
}
