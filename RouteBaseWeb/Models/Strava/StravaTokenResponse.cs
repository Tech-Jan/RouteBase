using System.Text.Json.Serialization;

namespace RouteBaseWeb.Models.Strava;

public class StravaTokenResponse
{
    [JsonPropertyName("token_type")]
    public string? TokenType { get; set; }

    [JsonPropertyName("expires_at")]
    public long ExpiresAt { get; set; }

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("refresh_token")]
    public string? RefreshToken { get; set; }

    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    [JsonPropertyName("athlete")]
    public StravaAthlete? Athlete { get; set; }
}

public class StravaAthlete
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("firstname")]
    public string? Firstname { get; set; }

    [JsonPropertyName("lastname")]
    public string? Lastname { get; set; }
}
