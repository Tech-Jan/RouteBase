using RouteBaseWeb.Constants;
using System.Text.RegularExpressions;
using RouteBaseWeb.Services.Sharing;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Sharing;

/// <summary>
/// The share CSP's map-host allowlist is a C# copy of the frontend's basemap registry. This is the guard
/// against the two drifting.
///
/// It exists because they DID drift: the first version of the policy allowed OpenStreetMap raster tiles
/// and the S3 elevation DEM but not <c>tiles.openfreemap.org</c>, which serves the DEFAULT basemap. Every
/// map on every share page failed to load, and the only symptom was a console error — no test failed, no
/// request 500'd, and the page otherwise rendered fine. A silent, browser-only break is exactly the kind
/// a unit test has to catch.
/// </summary>
public class ShareCspHostParityTests
{
    /// <summary>Frontend files that name a map host. Add one here when a new provider is introduced.</summary>
    private static readonly string[] SourceFiles =
    [
        "src/js/core/basemap-registry.ts",
        "src/js/maplibre/terrarium-dem.ts",
        "src/js/maplibre/maplibre-helpers.ts",
    ];

    /// <summary>
    /// Self-hosted style JSON. The registry points at these by same-origin path (`/map-styles/…`), so the
    /// third-party tile host they fetch is one level down and invisible to a scan of the TS alone — which
    /// is exactly how <c>server.arcgisonline.com</c> got left out of the CSP and broke the satellite
    /// basemap on every share page. Scanned as a DIRECTORY so a new style file is covered on arrival.
    /// </summary>
    private const string StyleJsonDirectory = "wwwroot/map-styles";

    /// <summary>
    /// Walks up from the test binary to the repo, so this does not depend on the working directory the
    /// runner happens to use.
    /// </summary>
    private static string WebRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "RouteBaseWeb");
            if (Directory.Exists(Path.Combine(candidate, "src", "js"))) return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the RouteBaseWeb project from " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> SourcePaths()
    {
        var root = WebRoot();
        foreach (var relative in SourceFiles)
        {
            var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"{relative} has moved — update SourceFiles in this test.");
            yield return path;
        }

        var styleDir = Path.Combine(root, StyleJsonDirectory.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(Directory.Exists(styleDir), $"{StyleJsonDirectory} has moved — update this test.");

        var styles = Directory.GetFiles(styleDir, "*.json");
        Assert.NotEmpty(styles);
        foreach (var path in styles) yield return path;
    }

    private static IEnumerable<string> HostsReferencedByFrontend()
    {
        foreach (var path in SourcePaths())
        {
            // Attribution ANCHORS are stripped first: `<a href="https://registry.opendata.aws/…">` is a
            // link the user may click, not a resource the page fetches. CSP fetch directives do not govern
            // navigation, so allowlisting those hosts would widen the policy for no reason. Only URLs the
            // map actually requests should reach the comparison below. In the style JSON the same anchor
            // appears inside a string with escaped quotes (\"), hence the optional backslashes.
            var source = Regex.Replace(File.ReadAllText(path), @"<a\s+href=\\?""[^""\\]*", "", RegexOptions.IgnoreCase, RegexDefaults.MatchTimeout);

            foreach (Match m in Regex.Matches(source, @"https://([a-zA-Z0-9.\-]+)", RegexOptions.None, RegexDefaults.MatchTimeout))
                yield return m.Groups[1].Value;
        }
    }

    /// <summary>`a.tile.openstreetmap.org` is covered by the allowlist entry `https://*.tile.openstreetmap.org`.</summary>
    private static bool IsAllowed(string host, IReadOnlyList<string> allowed)
    {
        foreach (var entry in allowed)
        {
            var pattern = entry["https://".Length..];
            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                if (host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    [Fact]
    public void EveryMapHostTheFrontendUses_IsAllowedByTheShareCsp()
    {
        var allowed = ShareResponseHeaders.AllowedMapHosts;

        var missing = HostsReferencedByFrontend()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(h => !IsAllowed(h, allowed))
            .OrderBy(h => h, StringComparer.Ordinal)
            .ToArray();

        Assert.True(missing.Length == 0,
            "The frontend fetches from host(s) the share CSP does not allow, so the map will silently fail "
            + "to load on every share page:\n  " + string.Join("\n  ", missing)
            + "\nAdd them to ShareResponseHeaders.MapHosts, or exclude the file from SourceFiles here if "
            + "the host is not a map fetch.");
    }

    /// <summary>
    /// Guards the guard: if the regex or the file list stopped matching, the test above would pass
    /// vacuously while allowing anything.
    /// </summary>
    [Fact]
    public void TheParityCheck_ActuallyFindsHosts()
    {
        var hosts = HostsReferencedByFrontend().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        Assert.NotEmpty(hosts);
        Assert.Contains("tiles.openfreemap.org", hosts, StringComparer.OrdinalIgnoreCase);

        // From the style JSON, not the TS — proves the directory scan is actually contributing.
        Assert.Contains("server.arcgisonline.com", hosts, StringComparer.OrdinalIgnoreCase);

        // SVC-11: terrarium-dem.ts now names the virtual-hosted bucket host, not path-style s3.amazonaws.com.
        Assert.Contains("elevation-tiles-prod.s3.amazonaws.com", hosts, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The default basemap specifically — the one whose omission broke every share map.</summary>
    [Fact]
    public void TheDefaultBasemapHost_IsAllowed()
    {
        Assert.True(IsAllowed("tiles.openfreemap.org", ShareResponseHeaders.AllowedMapHosts));
    }

    /// <summary>
    /// SVC-11: the terrarium DEM allowance is scoped to the ONE bucket the map fetches from, not to the whole
    /// of <c>s3.amazonaws.com</c>. Regression guard — a revert to the broad host (or a `*.s3.amazonaws.com`
    /// wildcard) would let a share page load tiles from any AWS bucket and must fail here.
    /// </summary>
    [Fact]
    public void TerrariumDemHost_IsNarrowedToTheBucket_NotAllOfS3()
    {
        var allowed = ShareResponseHeaders.AllowedMapHosts;

        Assert.True(IsAllowed("elevation-tiles-prod.s3.amazonaws.com", allowed));
        Assert.False(IsAllowed("s3.amazonaws.com", allowed));
        Assert.False(IsAllowed("other-bucket.s3.amazonaws.com", allowed));
    }
}
