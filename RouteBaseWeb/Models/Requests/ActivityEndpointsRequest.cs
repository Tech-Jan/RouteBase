namespace RouteBaseWeb.Models.Requests;

/// <summary>
/// Request for the activities/bounds endpoint: the filtered activity ID set whose
/// start/end coordinates frame the initial map viewport.
/// </summary>
public class ActivityEndpointsRequest
{
    public List<int> ActivityIds { get; set; } = new();
}
