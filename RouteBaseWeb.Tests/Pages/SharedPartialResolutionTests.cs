using RouteBaseWeb.Constants;
using System.Text.RegularExpressions;
using Xunit;

namespace RouteBaseWeb.Tests.Pages;

/// <summary>
/// Razor resolves a partial name against the EXECUTING PAGE's directory, not the directory of the partial
/// that references it. So a partial living in Pages/Activities but rendered from Pages/Share/View.cshtml
/// looks for its children under /Pages/Share — and throws at runtime if they are not there.
///
/// That failure is invisible to the compiler, invisible to `dotnet build`, and only fires on one route
/// when one branch happens to render. It shipped once already. This pins the fix: every partial reference
/// inside the shared activity-detail render must be an absolute /Pages/... path, or live in Pages/Shared
/// (which resolves from anywhere).
/// </summary>
public class SharedPartialResolutionTests
{
    /// <summary>
    /// The activity-detail partials rendered from BOTH /Activities/Details and /s/{token}. Add a file here
    /// when it joins the shared render path.
    /// </summary>
    private static readonly string[] SharedRenderPartials =
    [
        "Pages/Activities/_ActivityDetailBody.cshtml",
        "Pages/Activities/_ActivityDetailScripts.cshtml",
        "Pages/Activities/_DetailsHeader.cshtml",
        "Pages/Activities/_ActivityPhotos.cshtml",
        // Segment-detail render path — same rule, same reason.
        "Pages/Segments/_SegmentStatsBody.cshtml",
        "Pages/Segments/_SegmentStatsHeader.cshtml",
        "Pages/Segments/_SegmentStatsKpis.cshtml",
        "Pages/Segments/_SegmentStatsMapPanel.cshtml",
        "Pages/Segments/_SegmentStatsCharts.cshtml",
        "Pages/Segments/_SegmentStatsEfforts.cshtml",
        "Pages/Segments/_SegmentStatsScripts.cshtml",
        "Pages/Segments/_SegmentStatsStyles.cshtml",
        // The share-link dialog, rendered from BOTH detail headers.
        "Pages/Shared/_ShareLinkDialog.cshtml",
    ];

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "RouteBase.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    private static string WebRoot() => Path.Combine(RepoRoot(), "RouteBaseWeb");

    /// <summary>Partial names referenced by a .cshtml file, in source order.</summary>
    private static List<string> PartialNamesIn(string cshtmlPath)
    {
        var source = File.ReadAllText(cshtmlPath);
        return Regex.Matches(source, "<partial\\s+name\\s*=\\s*\"(?<name>[^\"]+)\"", RegexOptions.None, RegexDefaults.MatchTimeout)
            .Select(m => m.Groups["name"].Value)
            .ToList();
    }

    private static bool ResolvesFromAnyDirectory(string partialName)
    {
        // An absolute path is unambiguous.
        if (partialName.StartsWith('/')) return true;

        // Pages/Shared is searched no matter which page is executing.
        var shared = Path.Combine(WebRoot(), "Pages", "Shared", $"{partialName}.cshtml");
        return File.Exists(shared);
    }

    [Fact]
    public void SharedRenderPartials_ReferenceOnlyUnambiguousPartials()
    {
        var offenders = new List<string>();

        foreach (var relative in SharedRenderPartials)
        {
            var path = Path.Combine(WebRoot(), relative);
            Assert.True(File.Exists(path), $"Shared render partial not found: {relative}");

            foreach (var name in PartialNamesIn(path))
                if (!ResolvesFromAnyDirectory(name))
                    offenders.Add($"{relative} → \"{name}\"");
        }

        Assert.True(offenders.Count == 0,
            "These partial references resolve against the executing page's directory, so they throw when "
            + "rendered from /s/{token} (Pages/Share). Use the absolute \"/Pages/Activities/_X.cshtml\" "
            + "form, or move the partial to Pages/Shared:\n  "
            + string.Join("\n  ", offenders));
    }

    [Fact]
    public void SharedRenderPartials_ActuallyReferenceSomething()
    {
        // Anti-vacuity: if the regex stopped matching (tag-helper syntax changed, files renamed), the test
        // above would pass while checking nothing.
        var total = SharedRenderPartials
            .Sum(r => PartialNamesIn(Path.Combine(WebRoot(), r)).Count);

        Assert.True(total >= 4, $"Expected several partial references across the shared render path, found {total}.");
    }

    [Fact]
    public void SharePage_ReferencesTheSharedBodyAndScripts()
    {
        // The share view must render the SAME partials as the canonical page — that is the whole point of
        // the extraction. If it stops, the two have forked.
        var names = PartialNamesIn(Path.Combine(WebRoot(), "Pages/Share/View.cshtml"));

        Assert.Contains("/Pages/Activities/_ActivityDetailBody.cshtml", names, StringComparer.Ordinal);
        Assert.Contains("/Pages/Activities/_ActivityDetailScripts.cshtml", names, StringComparer.Ordinal);
        // ...and the segment branch renders the segment page's partials, for the same reason.
        Assert.Contains("/Pages/Segments/_SegmentStatsBody.cshtml", names, StringComparer.Ordinal);
        Assert.Contains("/Pages/Segments/_SegmentStatsScripts.cshtml", names, StringComparer.Ordinal);
    }
}
