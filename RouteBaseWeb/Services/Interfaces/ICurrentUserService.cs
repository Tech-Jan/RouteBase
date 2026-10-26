namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// Scoped service that reads the current authenticated user from HttpContext.
/// Injected into services that need to know who the current user is.
/// </summary>
public interface ICurrentUserService
{
    /// <summary>Returns the current user's ID (OIDC 'sub' claim). THROWS when there is no authenticated
    /// user — right for the app's authorized surfaces, where "no user" is a bug worth failing on.</summary>
    string GetUserId();

    /// <summary>
    /// The current user's ID, or null when the request is anonymous. Never throws.
    ///
    /// Exists because anonymous is a NORMAL state on some surfaces and <see cref="GetUserId"/> treats it as
    /// a fault: <c>/s/{token}</c> serves anyone with the URL, and <c>/Error</c> is <c>[AllowAnonymous]</c>
    /// precisely so an anonymous share visitor who hits a server error sees the error page instead of a
    /// login redirect. Anything rendered by a LAYOUT runs on those pages too, so it must ask this question
    /// without an exception being the answer — otherwise a failure inside the error page's own layout turns
    /// every error into an unrenderable one.
    /// </summary>
    string? TryGetUserId();

    /// <summary>Returns the display name, or null if not available.</summary>
    string? GetDisplayName();

    /// <summary>Returns true if the current user is an admin.</summary>
    bool IsAdmin();

    /// <summary>
    /// Sets an explicit user ID override for background/non-HTTP contexts.
    /// Only applies within the current scoped lifetime.
    /// </summary>
    void SetUserIdOverride(string userId);

    /// <summary>
    /// Scoped, RESTORING impersonation: acts as <paramref name="userId"/> until the returned handle is
    /// disposed, then puts back whatever was in effect before.
    ///
    /// Distinct from <see cref="SetUserIdOverride"/>, which is one-way and lasts the whole scope. That is
    /// right for a queue worker (the scope IS the operation) and wrong for a request that must widen for
    /// ONE read and then stop looking like the owner — the share slice's case, where a leaked override
    /// would reach Pages/Activities/Details.cshtml.cs's IsOwner check and render owner controls on an
    /// anonymous page.
    ///
    /// Not async-flow-safe: this is a plain field on a scoped service, so it does not propagate into a
    /// child DI scope and IS visible to anything else running concurrently in the same request scope.
    /// Request work here is sequential over a shared DbContext, which is what makes that safe — keep it.
    /// </summary>
    IDisposable BeginUserScope(string userId);
}
