using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sharing;

namespace RouteBaseWeb.Services.Sharing;

/// <summary>Outcome of a mint attempt. Failures are deliberately coarse — the controller maps them to
/// 404/400 without telling a caller which resources exist.</summary>
public enum ShareMintOutcome
{
    Ok,

    /// <summary>Not the caller's resource, or no such resource. One value on purpose: distinguishing them
    /// would turn mint into an existence oracle for other people's activities.</summary>
    NotFound,

    /// <summary>Unrecognized kind or TTL preset.</summary>
    BadRequest,

    /// <summary>Too many live links already exist for this resource.</summary>
    TooMany,
}

/// <summary>Mint result. <see cref="Token"/> is populated only when <see cref="Outcome"/> is Ok, and is
/// the only time the plaintext exists outside the caller's browser.</summary>
public sealed record ShareMintResult(
    ShareMintOutcome Outcome,
    string? Token = null,
    int Id = 0,
    DateTime ExpiresAt = default)
{
    public static ShareMintResult Fail(ShareMintOutcome outcome) => new(outcome);
}

public interface IShareLinkService
{
    /// <summary>
    /// Mints a link for a resource the caller OWNS. Ownership, not visibility: being able to see a
    /// friend's activity must not let you publish it to the internet on their behalf.
    /// </summary>
    Task<ShareMintResult> MintAsync(
        string kind, int resourceId, string? ttlName, string userId, CancellationToken ct);

    /// <summary>
    /// The caller's links for one resource, newest first, plus the resource's lifetime open count. Never
    /// includes a token.
    ///
    /// The list is CAPPED (see <c>ShareLinkService.MaxEndedLinksListed</c>): every active link, plus the
    /// most recent ended ones. Opened links are never swept, so an old resource can accumulate more dead
    /// rows than a dialog can usefully show — the total covers them all, the list shows the recent ones.
    /// </summary>
    Task<ShareLinkListResponse> ListAsync(
        string kind, int resourceId, string userId, CancellationToken ct);

    /// <summary>
    /// EVERY resource id of this kind that <paramref name="userId"/> has EVER shared, mapped to its
    /// lifetime open count and whether a link is live right now — one constant-shaped query, whatever the
    /// caller is rendering. Presence answers "was it ever shared?", <c>Active</c> answers "is it shared
    /// now?", and <c>Opens</c> answers "how much?", so a card needs one lookup for all three and cannot
    /// show a badge whose number disagrees with it.
    ///
    /// Opens are LIFETIME — summed across revoked and expired links too, not just live ones. Revoking a
    /// link must not reset a ride's view count, which is also why the sweep keeps any link that was ever
    /// opened. A resource whose only links died still appears here, with <c>Active</c> false.
    ///
    /// Deliberately takes no id list. Passing the page's ids looked tighter but made the SQL grow with the
    /// list (EF binds ONE parameter per element, so an unpaginated 2000-card feed emitted ~67 kB of SQL
    /// and a different query string for every distinct count, defeating the statement cache). The result
    /// set here is bounded by the user's own link count instead — the mint cap bounds the live ones, and
    /// only opened links survive the sweep — and Share_Link is indexed on user_id.
    ///
    /// Owner-scoped in the WHERE clause, not by the caller: a friend's card must never reveal that they
    /// published something.
    ///
    /// <paramref name="kind"/> names the SURFACE being badged, not one wire kind: asking for
    /// <c>activity</c> counts live-recording links too, because both publish the same ride's map and the
    /// card cannot show two different truths about one row. Unknown kinds return empty.
    /// </summary>
    Task<IReadOnlyDictionary<int, ShareOpens>> ShareOpensAsync(
        string kind, string userId, CancellationToken ct);

    /// <summary>
    /// Every link of <paramref name="userId"/>'s that is LIVE right now, across all four kinds, newest
    /// first, each named by the resource it publishes.
    ///
    /// The safety inventory: the per-resource dialog and the card badge both answer "is THIS shared?",
    /// which cannot answer "what am I publishing?" — a link on a three-year-old ride is invisible until you
    /// scroll to its card. Live-only for the same reason: a revoked link is not something a stranger can
    /// open, and listing dead rows would bury the ones that matter.
    ///
    /// Returns the caller's WHOLE live set, not a page. The row count is bounded by liveness and the mint
    /// cap, and a truncated safety inventory is worse than none — it would read as "that's everything".
    /// </summary>
    Task<ShareLinkInventoryResponse> ActiveLinksAsync(string userId, CancellationToken ct);

    /// <summary>
    /// Soft-revokes one of the caller's links. False when it does not exist or is not theirs — the
    /// controller answers 404 to both, so revoke is not an existence oracle either.
    /// </summary>
    Task<bool> RevokeAsync(int id, string userId, CancellationToken ct);
}
