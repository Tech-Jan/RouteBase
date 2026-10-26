namespace RouteBaseWeb.Auth;

/// <summary>
/// Constants for the share-link authentication scheme. One place, so scheme/policy/claim names are
/// greppable and a typo is a compile error rather than a silently unauthenticated endpoint.
/// </summary>
public static class ShareLinkDefaults
{
    public const string AuthenticationScheme = "ShareLink";

    /// <summary>The ONLY authorization policy that admits a share principal. Endpoints opt in by name.</summary>
    public const string ReadPolicy = "ShareLinkRead";

    /// <summary>
    /// Policy for minting/listing/revoking share links: an interactive human session only.
    ///
    /// Expressed as "authenticated, not a share principal, not an API key" rather than by naming the
    /// cookie scheme (the ApiKeyController approach). Naming the cookie scheme breaks in local mode,
    /// where only the "Local" scheme is registered and the cookie scheme does not exist at all.
    /// </summary>
    public const string MintPolicy = "ShareLinkMint";

    // No RateLimitPolicy constant: share rate limiting is a GlobalLimiter keyed on the presence of
    // RouteTokenKey, not a named policy endpoints must remember to opt into. See Program.cs.

    /// <summary>Route-value key carrying the token. The handler reads the token from the ROUTE only —
    /// never a header, never a cookie — so a grant cannot ride along on a request the visitor did not
    /// explicitly address to a /s/{token} URL.</summary>
    public const string RouteTokenKey = "shareToken";

    /// <summary>Carries the Share_Link row id. Used ONLY to resolve the grant — never as a query identity.</summary>
    public const string TokenIdClaim = "rb:share_token_id";

    /// <summary>
    /// The query identity for EVERY share request. Constant, not per-token, and deliberately not the
    /// owner's id.
    ///
    /// Not the owner's: every Owned*/Visible* path a share request reaches unanticipated then returns
    /// empty and the endpoint 404s (verified against VisibilityService — Owned* match nothing, Visible*
    /// reduce to released-and-public, CanViewItemAsync denies). Fail-closed by construction.
    ///
    /// Constant rather than share:{tokenId}: ActivityGpsService and SegmentEfforts embed the viewer id in
    /// cache keys, so a per-token identity would create one cache namespace per live token — the CA-07
    /// failure mode ApiKeyAuthenticationHandler already had to defend against. It costs nothing to
    /// collapse them, because VisibleActivities("share:anon") reduces to the same released-and-public set
    /// for every token. Per-token information lives in TokenIdClaim instead.
    ///
    /// Contains ':' so it can never collide with an OIDC sub or LocalModeOptions.UserId.
    /// </summary>
    public const string ViewerUserId = "share:anon";
}
