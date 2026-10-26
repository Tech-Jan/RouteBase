namespace RouteBaseWeb.Constants;

/// <summary>Keys for values stored in <c>HttpContext.Session</c>.</summary>
public static class SessionKeys
{
    /// <summary>CSRF nonce for the Strava OAuth flow — written when building the authorize URL,
    /// verified against the echoed <c>state</c> on the callback (PS-02).</summary>
    public const string StravaOAuthState = "StravaOAuthState";
}
