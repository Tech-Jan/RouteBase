using Microsoft.AspNetCore.Authentication;

namespace RouteBaseWeb.Auth;

/// <summary>
/// A failed remote login — IdP token-endpoint timeout, stale correlation from a back-button re-POST —
/// is environmental, not a bug, yet unhandled it surfaces as an AuthenticationFailureException 500
/// (Error_Log, 2026-07-22). Bounce to the manual login page instead. Loop-safe: /Auth/Login is
/// anonymous and only challenges on the user's click, so a flaky IdP yields a retry button, not a
/// redirect loop through the provider. Logged at Warning on purpose: the Error_Log mirror stores
/// Error+ only, and these rows carry no bug signal.
/// </summary>
public static class OidcRemoteFailureEvents
{
    public static Task Handle(RemoteFailureContext context)
    {
        context.HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(OidcRemoteFailureEvents))
            .LogWarning(context.Failure, "Remote login failed; redirecting to the login page.");

        // The post-login destination came from our own Challenge (Login.OnPost already validated it),
        // but this is still a redirect target built from request state — re-check locality.
        var returnUrl = context.Properties?.RedirectUri;
        var target = returnUrl != null
            && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//", StringComparison.Ordinal)
            ? $"/Auth/Login?returnUrl={Uri.EscapeDataString(returnUrl)}"
            : "/Auth/Login";

        context.Response.Redirect(target);
        context.HandleResponse();
        return Task.CompletedTask;
    }
}
