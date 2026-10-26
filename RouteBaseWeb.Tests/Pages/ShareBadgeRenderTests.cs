using RouteBaseWeb.Models.Sharing;
using RouteBaseWeb.Tests.Helpers;
using Xunit;

namespace RouteBaseWeb.Tests.Pages;

/// <summary>
/// The card badge (_ShareBadge.cshtml). One partial serves the dashboard, segment and race lists, so the
/// "does this ride say it is published, how loudly, and is it still" question is pinned once, here.
/// </summary>
public class ShareBadgeRenderTests : IClassFixture<RazorPartialRenderer>
{
    private const string BadgePath = "/Pages/Shared/_ShareBadge.cshtml";

    private readonly RazorPartialRenderer _renderer;

    public ShareBadgeRenderTests(RazorPartialRenderer renderer) => _renderer = renderer;

    [Fact]
    public async Task NotShared_RendersNothingAtAll()
    {
        // Null, not 0: a resource that was never shared must produce no markup, so call sites need no @if
        // and an unshared card gains no stray element to style around.
        var html = await _renderer.RenderAsync(BadgePath, new ShareBadgeModel(null, "activity-card-share"));

        Assert.DoesNotContain("bi-share", html, StringComparison.Ordinal);
        Assert.True(string.IsNullOrWhiteSpace(html));
    }

    [Fact]
    public async Task SharedButNeverOpened_ShowsTheGlyphWithoutANumber()
    {
        // 0 is "shared, nobody has been yet" — distinct from "not shared". Rendering the digit would read
        // as a failure state; the title carries the nuance instead.
        var html = await _renderer.RenderAsync(BadgePath, new ShareBadgeModel(new ShareOpens(0, Active: true), "activity-card-share"));

        Assert.Contains("bi-share", html, StringComparison.Ordinal);
        Assert.DoesNotContain("share-badge-count", html, StringComparison.Ordinal);
        Assert.Contains("not opened yet", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Opened_ShowsTheCountBesideTheGlyph()
    {
        var html = await _renderer.RenderAsync(BadgePath, new ShareBadgeModel(new ShareOpens(12, Active: true), "seg-card-share"));

        Assert.Contains("bi-share", html, StringComparison.Ordinal);
        Assert.Contains("share-badge-count", html, StringComparison.Ordinal);
        Assert.Contains(">12<", html, StringComparison.Ordinal);
        Assert.Contains("opened 12 times", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CarriesTheCallingListsOwnModifierClass()
    {
        // Each card list sizes and spaces its meta row differently, so the modifier has to survive the
        // move into a shared partial — dropping it would leave three lists rendering one geometry.
        var html = await _renderer.RenderAsync(BadgePath, new ShareBadgeModel(new ShareOpens(3, Active: true), "race-card-share"));

        Assert.Contains("race-card-share", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SharingEnded_KeepsTheCount_DimsIt_AndDropsTheTooltip()
    {
        // The point of the whole feature: revoking or expiring a link does not un-happen the views. What
        // changes is the styling — and the tooltip goes entirely, rather than gaining "was shared" prose.
        var html = await _renderer.RenderAsync(
            BadgePath, new ShareBadgeModel(new ShareOpens(12, Active: false), "activity-card-share"));

        Assert.Contains("share-badge--past", html, StringComparison.Ordinal);
        Assert.Contains(">12<", html, StringComparison.Ordinal);
        Assert.DoesNotContain("title=", html, StringComparison.Ordinal);
        Assert.DoesNotContain("aria-label=", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SharingEndedWithoutAnyOpens_RendersNothingAtAll()
    {
        // No live link to warn about and no views to remember. (The grid still calls this row "previously
        // shared" — that column answers "was it published?", the badge answers "how many views?".)
        var html = await _renderer.RenderAsync(
            BadgePath, new ShareBadgeModel(new ShareOpens(0, Active: false), "activity-card-share"));

        Assert.True(string.IsNullOrWhiteSpace(html));
    }

    [Fact]
    public async Task ActiveBadge_IsNotMarkedAsPast()
    {
        var html = await _renderer.RenderAsync(
            BadgePath, new ShareBadgeModel(new ShareOpens(4, Active: true), "activity-card-share"));

        Assert.DoesNotContain("share-badge--past", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "opened once")]
    [InlineData(2, "opened 2 times")]
    public async Task TitleReadsNaturallyAtOne(int opens, string expected)
    {
        var html = await _renderer.RenderAsync(BadgePath, new ShareBadgeModel(new ShareOpens(opens, Active: true), "activity-card-share"));

        Assert.Contains(expected, html, StringComparison.Ordinal);
    }
}
