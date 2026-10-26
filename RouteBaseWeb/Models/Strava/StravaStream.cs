using System.Text.Json.Serialization;

namespace RouteBaseWeb.Models.Strava;

public class StravaStream
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("data")]
    public List<object>? Data { get; set; }

    [JsonPropertyName("series_type")]
    public string? SeriesType { get; set; }

    [JsonPropertyName("original_size")]
    public int OriginalSize { get; set; }

    [JsonPropertyName("resolution")]
    public string? Resolution { get; set; }
}
