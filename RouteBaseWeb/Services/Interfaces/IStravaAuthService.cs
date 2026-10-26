using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Strava;

namespace RouteBaseWeb.Services.Interfaces;

public interface IStravaAuthService
{
    /// <summary>Returns true if a Strava token exists in the database.</summary>
    Task<bool> IsAuthenticatedAsync();

    /// <summary>Returns the athlete ID from the stored token, or null if not authenticated.</summary>
    Task<StravaAthlete?> GetAthleteAsync();

    /// <summary>Builds the Strava OAuth redirect URL. When <paramref name="state"/> is set it is
    /// appended as the CSRF nonce echoed back to the callback for verification.</summary>
    string GetAuthorizationUrl(string redirectUri, string? clientId, string? state = null);

    /// <summary>Exchanges an OAuth authorization code for tokens and persists them.</summary>
    Task<StravaTokenResponse?> ExchangeCodeForTokenAsync(string code);

    /// <summary>Removes all Strava credential rows (token + API key).</summary>
    Task DisconnectAsync();

    /// <summary>Returns the saved Strava client ID, or null if none saved.</summary>
    Task<string?> GetSavedClientIdAsync();

    /// <summary>Persists (upserts) the Strava API credentials.</summary>
    Task SaveCredentialsAsync(string clientId, string clientSecret);

    /// <summary>Returns a valid access token, refreshing it if expired. Returns null if not authenticated.</summary>
    Task<string?> GetValidAccessTokenAsync();
}
