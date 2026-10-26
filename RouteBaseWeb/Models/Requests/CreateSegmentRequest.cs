using RouteBaseWeb.Models;

namespace RouteBaseWeb.Models.Requests;

public class CreateSegmentRequest
{
    public string SegmentName { get; set; } = "";
    public string? ActivityType { get; set; }
    public List<SegmentGpsPoint> GpsPoints { get; set; } = new();
}
