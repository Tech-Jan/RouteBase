namespace RouteBaseWeb.Auth;

/// <summary>API clients authenticate with X-Api-Key; an OIDC browser redirect is
/// meaningless for them — short-circuit the challenge to a plain 401 instead.</summary>
public static class ApiClientChallengeGate
{
    public static bool ShortCircuit(HttpContext context)
    {
        if (!context.Request.Headers.ContainsKey("X-Api-Key"))
            return false;
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return true;
    }
}
