namespace RouteBaseWeb.Models.Sharing;

/// <summary>
/// Copy for the shared share-link dialog (<c>Pages/Shared/_ShareLinkDialog.cshtml</c>).
///
/// Deliberately carries no resource id or kind: the dialog markup is identical for every resource, and the
/// wire kind + id are passed to <c>setupShareLinks</c> in TypeScript. Putting them here too would be two
/// sources of truth for the same fact.
/// </summary>
public sealed record ShareLinkDialogModel(string Title, string Help)
{
    public static readonly ShareLinkDialogModel ForActivity = new(
        "Share this activity",
        "Anyone with the link can view this activity — including its map, charts and replay — without "
        + "signing in. Links always expire, and you can revoke one at any time.");

    /// <summary>
    /// The wording is explicit that only the owner's own efforts appear: a recipient seeing a short
    /// leaderboard should understand it is by design, not a bug, and an owner should know they are not
    /// publishing anyone else's rides.
    /// </summary>
    public static readonly ShareLinkDialogModel ForSegment = new(
        "Share this segment",
        "Anyone with the link can view this segment — its map, your own attempts, the charts and the "
        + "replay — without signing in. Other riders' efforts are never included. Links always expire, "
        + "and you can revoke one at any time.");

    /// <summary>
    /// The wording is explicit that a live-share exposes the moving map to ANYONE with the link, regardless
    /// of the recording's privacy setting — live location is sensitive, and the owner should understand
    /// exactly what the link publishes and that it stops instantly on revoke.
    /// </summary>
    public static readonly ShareLinkDialogModel ForLiveRecording = new(
        "Share this live recording",
        "Anyone with the link can watch this recording's live map — no sign-in — for as long as the link "
        + "is valid. It shows the moving track and your display name, nothing else, and works regardless of "
        + "the recording's privacy setting. Links always expire, and you can revoke one at any time.");

    /// <summary>
    /// The wording names where the race redaction line actually falls, because a race payload is the one
    /// that would otherwise carry other people's data. Since 2026-07-24 that line is GEOMETRY, not identity
    /// (see <c>ShareRaceContracts</c>): the imported start list ships verbatim, so the owner must know they
    /// are publishing entrant names — while adding a friend's ride to the field still never publishes it.
    /// </summary>
    public static readonly ShareLinkDialogModel ForRace = new(
        "Share this race",
        "Anyone with the link can watch this race replay — the course, the full results field and your own "
        + "ride — without signing in. Entrants appear as the results file has them, names and bibs "
        + "included, but no rider's GPS except your own is ever shared. Links always expire, and you can "
        + "revoke one at any time.");
}
