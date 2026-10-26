using System.Net;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;

namespace RouteBaseWeb.Configuration;

/// <summary>
/// Safety rails for localhost mode. Local mode disables authentication, so two invariants must hold:
/// the switch can only be flipped by the physical desktop file (never remotely), and the app may only
/// ever bind to loopback. Both checks are pure and unit-tested (LocalModeGuardTests).
/// </summary>
public static class LocalModeGuard
{
    /// <summary>
    /// Reads the localhost-mode switch from ONLY the <c>appsettings.Local.json</c> in
    /// <paramref name="basePath"/> (next to the exe). Environment variables, command-line args, and the
    /// main appsettings.json are all intentionally excluded — so nothing can turn authentication off on the
    /// server remotely (a stray <c>LocalMode__Enabled</c> env var, an injected config value, an edit to the
    /// committed appsettings.json). The switch exists only where the desktop artifact ships that one file.
    /// </summary>
    public static LocalModeOptions ReadFromFile(string basePath) =>
        new ConfigurationBuilder()
            .SetBasePath(basePath)
            .AddJsonFile(LocalModeOptions.SettingsFileName, optional: true)
            .Build()
            .GetSection(LocalModeOptions.SectionName)
            .Get<LocalModeOptions>() ?? new LocalModeOptions();

    /// <summary>
    /// True if every bind address the configuration can produce is loopback (or none is configured).
    /// Kestrel binds from MORE sources than the <c>urls</c> key: a <c>Kestrel:Endpoints:*:Url</c> section
    /// never populates it, and the <c>ASPNETCORE_HTTP(S)_PORTS</c> shorthands mean <c>http://*:port</c> —
    /// always non-loopback, so their mere presence is refused. Checking only <c>urls</c> waved a
    /// LAN-exposed no-auth instance through.
    /// </summary>
    public static bool IsLoopbackOnly(IConfiguration config)
    {
        if (!IsLoopbackOnly(config["urls"])) return false;

        if (!string.IsNullOrWhiteSpace(config["HTTP_PORTS"])
            || !string.IsNullOrWhiteSpace(config["HTTPS_PORTS"])) return false;

        foreach (var endpoint in config.GetSection("Kestrel:Endpoints").GetChildren())
            if (!IsLoopbackOnly(endpoint["Url"])) return false;

        return true;
    }

    /// <summary>
    /// True if every server URL in the string binds to loopback (or none is configured). Any non-loopback
    /// bind — <c>0.0.0.0</c>, <c>+</c>, <c>*</c>, a LAN IP, or a hostname — returns false so the app can
    /// refuse to start.
    /// </summary>
    public static bool IsLoopbackOnly(string? urls)
    {
        if (string.IsNullOrWhiteSpace(urls)) return true;

        foreach (var raw in urls.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string host;
            try { host = BindingAddress.Parse(raw).Host; }
            catch { return false; } // unparseable → treat as unsafe

            if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) continue;
            if (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip)) continue;
            return false;
        }
        return true;
    }
}
