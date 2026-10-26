using System.Security.Claims;
using RouteBaseWeb.Services.Interfaces;

namespace RouteBaseWeb.Services;

public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IConfiguration _configuration;
    private string? _userIdOverride;

    public CurrentUserService(
        IHttpContextAccessor httpContextAccessor,
        IConfiguration configuration)
    {
        _httpContextAccessor = httpContextAccessor;
        _configuration = configuration;
    }

    /// <summary>Worker scopes (no HttpContext) impersonate the operation's owner. Affects
    /// GetUserId() only — GetDisplayName()/IsAdmin() still read HttpContext and return
    /// null/false under an override; don't call them from worker code.</summary>
    public void SetUserIdOverride(string userId) => _userIdOverride = userId;

    /// <inheritdoc />
    public IDisposable BeginUserScope(string userId)
    {
        var previous = _userIdOverride;
        _userIdOverride = userId;
        return new UserScope(this, previous);
    }

    /// <summary>Restores the prior override on dispose — including on the exception path, which is the
    /// point: a throw mid-read must not leave the request impersonating the owner.</summary>
    private sealed class UserScope(CurrentUserService owner, string? previous) : IDisposable
    {
        public void Dispose() => owner._userIdOverride = previous;
    }

    public string GetUserId() =>
        TryGetUserId() ?? throw new InvalidOperationException("No authenticated user in current context.");

    /// <inheritdoc />
    public string? TryGetUserId()
    {
        if (_userIdOverride is not null)
            return _userIdOverride;

        var user = _httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
            return null;

        // An authenticated principal with no subject claim is a broken token, not an anonymous request —
        // but this method's contract is "never throws", so it reports the same "no user" either way and
        // lets GetUserId turn it into the exception.
        return user.FindFirstValue(ClaimTypes.NameIdentifier) ?? user.FindFirstValue("sub");
    }

    public string? GetDisplayName()
    {
        var user = _httpContextAccessor.HttpContext?.User;
        return user?.FindFirstValue("preferred_username")
               ?? user?.FindFirstValue("name")
               ?? user?.FindFirstValue(ClaimTypes.Name);
    }

    public bool IsAdmin()
    {
        var user = _httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated != true)
            return false;

        var adminGroup = _configuration["Authentik:AdminGroup"] ?? "routebase-admin";

        // Authentik sends groups as multiple 'groups' claims
        return user.FindAll("groups").Any(c => c.Value == adminGroup);
    }
}
