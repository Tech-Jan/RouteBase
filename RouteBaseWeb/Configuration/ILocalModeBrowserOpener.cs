using System.Diagnostics;

namespace RouteBaseWeb.Configuration;

/// <summary>
/// The one seam that turns "localhost mode has started" into a real browser window. It exists so a test
/// host can boot the real <c>Program.cs</c> in local mode without a browser opening on the developer's
/// machine: the launch is a side effect on the outside world, so it hangs off an interface like any other
/// I/O, not off an inline <see cref="Process"/> call that only an accident of hosting can suppress.
/// </summary>
public interface ILocalModeBrowserOpener
{
    /// <summary>Open <paramref name="url"/> in the user's default browser. Must never throw.</summary>
    void Open(string url, ILogger logger);
}

/// <summary>
/// Production implementation: hands the URL to the OS shell (<c>xdg-open</c> on Linux, the shell verb on
/// Windows). Failure is cosmetic — the server is already listening — so it only warns.
/// </summary>
public sealed class ShellBrowserOpener : ILocalModeBrowserOpener
{
    public void Open(string url, ILogger logger)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Localhost mode: could not auto-open the browser at {Url}; open it manually.", url);
        }
    }
}
