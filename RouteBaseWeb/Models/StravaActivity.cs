using System.Text.Json.Serialization;

namespace RouteBaseWeb.Models;

// Strava API response model
public class StravaActivity
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("sport_type")]
    public string? SportType { get; set; }

    [JsonPropertyName("start_date")]
    public DateTime StartDate { get; set; }

    [JsonPropertyName("start_date_local")]
    public DateTime StartDateLocal { get; set; }

    [JsonPropertyName("timezone")]
    public string? Timezone { get; set; }

    [JsonPropertyName("start_latlng")]
    public List<double>? StartLatLng { get; set; }

    [JsonPropertyName("end_latlng")]
    public List<double>? EndLatLng { get; set; }

    [JsonPropertyName("distance")]
    public double Distance { get; set; }

    [JsonPropertyName("moving_time")]
    public int MovingTime { get; set; }

    [JsonPropertyName("elapsed_time")]
    public int ElapsedTime { get; set; }

    [JsonPropertyName("total_elevation_gain")]
    public double TotalElevationGain { get; set; }

    [JsonPropertyName("elev_high")]
    public double? ElevHigh { get; set; }

    [JsonPropertyName("elev_low")]
    public double? ElevLow { get; set; }

    [JsonPropertyName("average_speed")]
    public double AverageSpeed { get; set; }

    [JsonPropertyName("max_speed")]
    public double MaxSpeed { get; set; }

    [JsonPropertyName("average_heartrate")]
    public double? AverageHeartrate { get; set; }

    [JsonPropertyName("max_heartrate")]
    public double? MaxHeartrate { get; set; }

    [JsonPropertyName("average_cadence")]
    public double? AverageCadence { get; set; }

    [JsonPropertyName("has_heartrate")]
    public bool HasHeartrate { get; set; }
}

public class StravaDetailedActivity : StravaActivity
{
    [JsonPropertyName("calories")]
    public double? Calories { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("average_watts")]
    public double? AverageWatts { get; set; }

    [JsonPropertyName("max_watts")]
    public double? MaxWatts { get; set; }

    [JsonPropertyName("device_name")]
    public string? DeviceName { get; set; }
}
