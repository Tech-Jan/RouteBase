namespace RouteBaseWeb.Models;

/// <summary>
/// Thrown when Strava returns HTTP 429 (Too Many Requests).
/// Allows callers to surface a user-friendly rate-limit message
/// rather than treating it as "zero activities found".
/// </summary>
public class StravaRateLimitException : ProviderRateLimitException
{
    public StravaRateLimitException()
        : base("Strava API rate limit reached. Please wait 15 minutes before trying again.")
    {
    }
}
