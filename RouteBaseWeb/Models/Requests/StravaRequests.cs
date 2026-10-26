using RouteBaseWeb.Models;

namespace RouteBaseWeb.Models.Requests;

public class ImportAllRequest
{
    public DateTime? After { get; set; }
}

public class StravaAuthUrlRequest
{
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
}
