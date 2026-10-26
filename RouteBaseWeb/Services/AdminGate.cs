using RouteBaseWeb.Configuration;
using RouteBaseWeb.Services.Interfaces;

namespace RouteBaseWeb.Services;

/// <summary>
/// Is the current user an app admin? Backed by the <c>Admin:Users</c> config list (project ⏸1
/// rec A) — verifiable in code alone, no dependency on the Authentik admin group being
/// provisioned server-side. Isolated here so switching to the dormant
/// <see cref="ICurrentUserService.IsAdmin"/> (⏸A1-1, Authentik group claim) stays a one-line
/// change for all consumers (/Admin/Errors gate, sidebar nav entry).
/// </summary>
public interface IAdminGate
{
    bool IsAdminUser();
}

public class AdminGate : IAdminGate
{
    private readonly IConfiguration _configuration;
    private readonly ICurrentUserService _currentUser;
    private readonly LocalModeOptions _localMode;
    private string[]? _adminUsers; // scoped service → per-request bind, not per-call (layout asks twice per render)

    public AdminGate(IConfiguration configuration, ICurrentUserService currentUser, LocalModeOptions localMode)
    {
        _configuration = configuration;
        _currentUser = currentUser;
        _localMode = localMode;
    }

    // Localhost mode: admin is whoever is authenticated AS the single local user — it owns the machine
    // (sees /Admin/Errors + nav). Deliberately NOT "Enabled alone": share-link visitors are non-local
    // principals in the same pipeline (ShareLinkDefaults.ViewerUserId, which contains ':' precisely so
    // it can never collide with LocalModeOptions.UserId) and must never pass this gate.
    public bool IsAdminUser() =>
        _localMode.Enabled
            ? _currentUser.GetUserId() == LocalModeOptions.UserId
            : (_adminUsers ??= _configuration.GetSection("Admin:Users").Get<string[]>() ?? [])
                .Contains(_currentUser.GetUserId());
}
