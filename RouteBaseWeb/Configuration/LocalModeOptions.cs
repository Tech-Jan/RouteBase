namespace RouteBaseWeb.Configuration;

/// <summary>
/// Localhost mode: the app runs on one person's PC with no Authentik and no login. When
/// <see cref="Enabled"/>, the Authentik OIDC/cookie stack is replaced by a single always-authenticated
/// local user (<see cref="UserId"/>), that user is treated as admin, and the login/logout UI is hidden.
///
/// Off by default, so the server deployment is byte-identical and unaffected. It is switched on only in
/// the shipped desktop artifact via <c>appsettings.Local.json</c> (README, localhost mode) — never in the
/// checked-in config. Bound from the <c>"LocalMode"</c> section in the house style
/// (<c>Get&lt;T&gt;() ?? new()</c>), mirroring <see cref="RouteBaseWeb.Services.MemoryBudget"/>.
/// </summary>
public sealed record LocalModeOptions
{
    public const string SectionName = "LocalMode";

    /// <summary>The fixed identity every request runs as in local mode — owner of every row in the
    /// single-user DB. Stable across restarts so data persists; a constant so it stays grep-able.</summary>
    public const string UserId = "local";

    /// <summary>Display name for the one local user (header, owner labels).</summary>
    public const string DisplayName = "Local User";

    /// <summary>Scheme name the <c>LocalUserAuthenticationHandler</c> is registered under.</summary>
    public const string AuthenticationScheme = "Local";

    /// <summary>The switch file's name. One const for the two readers (Program.cs main-config add,
    /// LocalModeGuard's gate read) — renamed apart, the stray file would still override server config
    /// while the mode never enables. scripts/publish-local-win.ps1 copies it by literal name.</summary>
    public const string SettingsFileName = "appsettings.Local.json";

    /// <summary>When true, run without Authentik as the single local user. Default false = server mode.</summary>
    public bool Enabled { get; init; }
}
