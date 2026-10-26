using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Sharing;

/// <summary>
/// What one live token authorizes: exactly one resource, owned by one user, until one instant.
/// Resolved from the DB row on every request (never cached — see <c>ShareLinkAccess.ResolveAsync</c>).
///
/// This is the whole authority a share request carries. It is deliberately NOT an identity: the
/// principal minted alongside it is the constant <c>share:anon</c>, which owns nothing and sees only
/// public data, so anything not explicitly checked against this grant fails closed.
/// </summary>
public sealed record ShareGrant(
    int TokenId,
    ShareKind Kind,
    int ResourceId,
    string OwnerUserId,
    DateTime ExpiresAt)
{
    /// <summary>
    /// The assertion every share-reachable endpoint makes BEFORE widening. Both halves matter: an
    /// activity token must not widen a segment read of the same numeric id, and a token for track A
    /// must not widen track B. Route parameters are not trusted merely because the token authenticated.
    /// </summary>
    public bool Covers(ShareKind kind, int resourceId)
        => kind == Kind && resourceId == ResourceId;
}
