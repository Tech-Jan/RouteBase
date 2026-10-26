using RouteBaseWeb.Configuration;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace RouteBaseWeb.Tests.Hosting;

/// <summary>
/// Boots the real Program.cs in LOCALHOST mode. The mode switch is a physical file next to the exe by
/// design, so tests enable it the only way production can: <see cref="LocalSettingsFile"/> writes
/// <c>appsettings.Local.json</c> into the test bin dir BEFORE the (lazy) first boot, and deletes it in
/// the test's finally. Construct this factory only between those two calls.
/// </summary>
public class LocalModeAppFactory : WebApplicationFactory<Program>
{
    /// <summary>Records what production would have opened. Never launches anything.</summary>
    public RecordingBrowserOpener BrowserOpener { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "wwwroot"));

        builder.ConfigureAppConfiguration(config => config.AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            // Local mode refuses to start on any non-loopback bind source (LocalModeGuard). The test
            // process can inherit ASPNETCORE_URLS/HTTP(S)_PORTS from the shell, so pin all three bind
            // sources to loopback/empty for a deterministic boot.
            ["urls"] = "http://localhost:5054",
            ["HTTP_PORTS"] = "",
            ["HTTPS_PORTS"] = "",
        }));

        // MANDATORY, not a convenience: this boot runs the REAL Program.cs, whose ApplicationStarted hook
        // opens the local-mode URL in the developer's browser. TestServer binding no socket does NOT stop
        // it — app.Urls is served by the "urls" value pinned above — so `dotnet test` used to launch a
        // browser window per local-mode test. Substituting the opener is what keeps the suite headless.
        builder.ConfigureTestServices(services =>
            services.AddSingleton<ILocalModeBrowserOpener>(BrowserOpener));
    }
}

/// <summary>Test double for <see cref="ILocalModeBrowserOpener"/>: captures the URLs instead of opening them.</summary>
public sealed class RecordingBrowserOpener : ILocalModeBrowserOpener
{
    private readonly List<string> _opened = new();

    public IReadOnlyList<string> Opened
    {
        get { lock (_opened) return _opened.ToArray(); }
    }

    public void Open(string url, ILogger logger)
    {
        lock (_opened) _opened.Add(url);
    }
}

/// <summary>The physical appsettings.Local.json drop, shared by the A5/C6 pipeline tests.</summary>
public static class LocalSettingsFile
{
    public static readonly string FilePath =
        Path.Combine(AppContext.BaseDirectory, LocalModeOptions.SettingsFileName);

    public static void Write(string json) => File.WriteAllText(FilePath, json);

    public static void Delete()
    {
        if (File.Exists(FilePath)) File.Delete(FilePath);
    }
}
