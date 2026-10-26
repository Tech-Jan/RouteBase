namespace RouteBaseWeb.Models.Sharing;

/// <summary>
/// The "this is published" badge on a dashboard / segment / race card: the share glyph, plus how often the
/// resource has ever been opened through a share link.
///
/// Extracted at the THIRD copy, which is also the commit that gave the badge something to say — three cards
/// hand-rolling the same icon was tolerable, three hand-rolling the same icon AND the same open-count
/// wording is where the title text starts to drift.
///
/// The count is LIFETIME, so it outlives the link that earned it: revoking or letting a link expire dims the
/// badge, it does not reset the number (and a finished live share's views stay on the ride — same track_id
/// bucket). Ending a share is not an event that un-happens the views.
/// </summary>
/// <param name="Opens">
/// The resource's lifetime opens and whether a link is live, or null when it was never shared. Null and
/// <c>(0, active)</c> are different answers: null is "never published", 0 is "published, nobody has been".
/// </param>
/// <param name="CssClass">
/// The card's own modifier for the glyph (<c>activity-card-share</c> / <c>seg-card-share</c> /
/// <c>race-card-share</c>) — each card list sizes and spaces its meta row differently.
/// </param>
public sealed record ShareBadgeModel(ShareOpens? Opens, string CssClass)
{
    /// <summary>
    /// Whether to render anything at all. A resource that is no longer shared AND was never opened has
    /// nothing to say — no live link to warn about, no views to remember — so it renders as if it had never
    /// been shared. (The grid filter still calls it "previously shared": that column answers "was it
    /// published?", this badge answers "how many views?".)
    /// </summary>
    public bool IsShared => Opens is { } o && (o.Active || o.Opens > 0);

    /// <summary>True while a link is live — the bright badge. False is the dimmed "was shared" state.</summary>
    public bool IsActive => Opens?.Active ?? false;

    /// <summary>The lifetime count, suppressed at 0 by the view (a bare glyph already says "published").</summary>
    public int OpenCount => Opens?.Opens ?? 0;

    /// <summary>The past-state modifier, or empty. A property rather than an <c>@if</c> in the view: the
    /// badge's three states are decided in one place.</summary>
    public string StateClass => IsShared && !IsActive ? "share-badge--past" : "";

    /// <summary>
    /// Hover text, for the ACTIVE badge only. It names the number rather than just asserting "shared",
    /// because the number is the whole point: an unexpected count is how a leaked link announces itself.
    ///
    /// NULL once sharing has ended — deliberately no "was shared" prose. The dimmed glyph and the number
    /// already say it, and a tooltip on every past ride is noise the owner never asked a question about.
    /// Null rather than "": Razor omits an attribute whose value expression is null, so the ended badge
    /// carries no empty <c>title</c>/<c>aria-label</c> either.
    /// </summary>
    public string? Title => (IsActive, OpenCount) switch
    {
        (false, _) => null,
        (true, 0) => "Shared — an active link exists, not opened yet",
        (true, 1) => "Shared — opened once",
        (true, var n) => $"Shared — opened {n} times",
    };
}
