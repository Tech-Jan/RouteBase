using RouteBaseWeb.Models.Sync;

namespace RouteBaseWeb.Models.Gpx;

/// <summary>
/// Result of parsing a GPX file.
/// </summary>
public class GpxParseResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public ExternalActivitySummary? Summary { get; set; }
    public List<ExternalGpsPoint> GpsPoints { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}
