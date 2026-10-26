using RouteBaseWeb.Constants;
using System.Text.RegularExpressions;
using RouteBaseWeb.Models.Settings;
using RouteBaseWeb.Tests.Helpers;
using Xunit;

namespace RouteBaseWeb.Tests.Pages;

/// <summary>
/// Gates (c1) and (c2) — the settings island's DELIVERY, at partial level.
///
/// Today <c>_ProfileSettingsBootstrap.cshtml</c> emits the island only inside
/// <c>@if (!string.IsNullOrEmpty(Model.MapTilerApiKey))</c>, so a page with no MapTiler credential ships
/// no settings and every TS reader silently falls back to a default: a user's chart smoothing is ignored
/// because an unrelated MAP CREDENTIAL is missing. And it is rendered by three pages rather than by the
/// layouts, so "which pages get settings" is a per-page decision nobody can see.
///
/// (c1) pins the payload: the partial renders with no credential anywhere and still emits a complete
///      island. This is a RENDER, not a markup scan — the failure being guarded is "nothing came out",
///      which only rendering can observe.
/// (c2) pins the delivery point: the partial is rendered by the three LAYOUTS, which <c>_ViewStart</c> plus
///      the per-page <c>Layout =</c> assignments make a partition of every page, and by nothing else. A page
///      that renders it directly is the old per-page-decision model returning.
/// </summary>
public class AppSettingsBootstrapTests
{
    private const string PartialRelativePath = "Pages/Shared/_AppSettingsBootstrap.cshtml";
    private const string PartialViewPath = "/Pages/Shared/_AppSettingsBootstrap.cshtml";

    /// <summary>The layouts, which are the only permitted renderers of the settings island.</summary>
    private static readonly string[] Layouts =
    [
        "Pages/Shared/_Layout.cshtml",
        "Pages/Shared/_DashboardLayout.cshtml",
        "Pages/Shared/_ShareLayout.cshtml",
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

    // ─── (c1) the partial renders a complete island with no MapTiler credential ───

    /// <summary>
    /// The renderer has no app container and no configuration, so "no MapTiler key" is not something this
    /// test arranges — it is the only state available. That is exactly the condition under which the
    /// island must still appear.
    ///
    /// Rendered with <c>SettingsPayload.Defaults</c> because the partial takes the payload as its MODEL
    /// rather than injecting the provider — which is also what an anonymous share page is handed. That
    /// choice keeps this gate renderable with the plain <see cref="RazorPartialRenderer"/>: an
    /// <c>@inject</c>ing partial would need a service registered into the renderer, and its constructor
    /// cannot carry that seam (it is an xunit <c>IClassFixture</c> in four other classes, and xunit rejects
    /// both a second public constructor and a lone constructor with an optional parameter — each shape was
    /// tried and each failed 49 unrelated share-render tests).
    /// </summary>
    [Fact]
    public async Task ThePartial_RendersACompleteIsland_WithNoMapTilerCredential()
    {
        Assert.True(
            File.Exists(Path.Combine(WebRoot(), PartialRelativePath)),
            $"{PartialRelativePath} does not exist. The settings island is delivered by "
            + "_ProfileSettingsBootstrap.cshtml, which emits NOTHING without a MapTiler key — a map "
            + "credential gating chart preferences. This gate pins its replacement.");

        using var renderer = new RazorPartialRenderer();
        var html = await renderer.RenderAsync(PartialViewPath, SettingsPayload.Defaults);

        Assert.Contains("id=\"appSettings\"", html, StringComparison.Ordinal);

        // All three sections, each with a leaf — a section serialized as `null` or `{}` would otherwise pass.
        foreach (var key in new[]
        {
            "\"map\"", "\"hillshadeExaggeration\"",
            "\"charts\"", "\"speedSmoothingSeconds\"",
            "\"ui\"", "\"showDebugInfo\"",
        })
            Assert.Contains(key, html, StringComparison.Ordinal);

        // Ordinal on purpose: xunit's string Contains/DoesNotContain default to CurrentCulture, under which
        // some collations treat characters as ignorable-equal and the assert lies in both directions.
        //
        // The MapTiler key is a CREDENTIAL, not a preference. It rides its own island; letting it into the
        // settings payload is what coupled the two in the first place, and the payload now reaches
        // anonymous share viewers.
        Assert.DoesNotContain("mapTilerApiKey", html, StringComparison.Ordinal);
    }

    // ─── (c2) exactly the three layouts render it, and no page does ───

    /// <summary>Every .cshtml under Pages/ that references <paramref name="partialName"/>, repo-relative.</summary>
    private static string[] ViewsReferencing(string partialName)
    {
        var pages = Path.Combine(WebRoot(), "Pages");
        var referenceRe = new Regex($@"<partial\s+name\s*=\s*""(/Pages/Shared/)?{Regex.Escape(partialName)}(\.cshtml)?""", RegexOptions.None, RegexDefaults.MatchTimeout);

        return Directory.EnumerateFiles(pages, "*.cshtml", SearchOption.AllDirectories)
            .Where(f => referenceRe.IsMatch(File.ReadAllText(f)))
            .Select(f => Path.GetRelativePath(WebRoot(), f).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToArray();
    }

    [Fact]
    public void TheSettingsIsland_IsRenderedByTheThreeLayouts_AndByNoPage()
    {
        var actual = ViewsReferencing("_AppSettingsBootstrap");
        var expected = Layouts.OrderBy(f => f, StringComparer.Ordinal).ToArray();

        Assert.True(
            actual.SequenceEqual(expected, StringComparer.Ordinal),
            "The settings island must be rendered by the three layouts and by nothing else. _ViewStart plus "
            + "the per-page `Layout =` assignments make those three a partition of every page, so rendering "
            + "there is what makes \"every page gets settings\" true by construction instead of per-page "
            + "opt-in. A page in this list is the old model returning.\n"
            + "  expected:\n    " + string.Join("\n    ", expected) + "\n"
            + "  actual:\n    " + (actual.Length == 0 ? "(nothing renders it)" : string.Join("\n    ", actual)));
    }

    [Fact]
    public void NoView_RendersTheOldPerPageSettingsPartial()
    {
        var offenders = ViewsReferencing("_ProfileSettingsBootstrap");

        Assert.True(
            offenders.Length == 0,
            "_ProfileSettingsBootstrap is the per-page, MapTiler-key-gated delivery this redesign replaces. "
            + "Each of these picks its own subset of the settings and emits none of it on a keyless page:\n  "
            + string.Join("\n  ", offenders));
    }

    /// <summary>
    /// Guards the guards: a scan that found no .cshtml at all, or a reference regex that stopped matching
    /// the tag-helper syntax, would make both facts above pass while checking nothing.
    /// </summary>
    [Fact]
    public void ThePartialReferenceScan_ActuallyFindsViewsAndReferences()
    {
        var views = Directory.EnumerateFiles(Path.Combine(WebRoot(), "Pages"), "*.cshtml", SearchOption.AllDirectories).ToArray();
        Assert.True(views.Length > 30, $"Found only {views.Length} .cshtml files under Pages/ — the scan is broken.");

        // A partial every layout-independent page renders, so the regex is proven against a live reference.
        Assert.NotEmpty(ViewsReferencing("_ShareLinkDialog"));
    }
}
