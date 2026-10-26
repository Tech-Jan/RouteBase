namespace RouteBaseWeb.Models;

// Request/response contracts for ShareLinksController. Serialized camelCase (web defaults);
// wire shapes pinned by ShareLinksApiWireShapeTests. Each mirrors a TS type — edit both in the
// same commit.

/// <summary>
/// Body of POST /api/sharelinks. <c>Ttl</c> is a preset NAME ("24h" | "7d" | "30d"), never a timestamp —
/// see <see cref="Services.Sharing.ShareTtl"/> for why.
/// </summary>
public sealed record ShareLinkMintRequest(string Kind, int ResourceId, string Ttl);

/// <summary>
/// Response of POST /api/sharelinks. The ONLY time the plaintext token is ever emitted — only its SHA-256
/// is stored, so it cannot be shown again and does not appear on the list endpoint.
///
/// <c>Url</c> is RELATIVE ("/s/xxx") on purpose: an absolute URL would have to be built from
/// <c>Request.Host</c>, which is spoofable here (UseForwardedHeaders trusts any upstream), so a poisoned
/// Host header could hand the owner a share URL pointing at an attacker's domain.
/// </summary>
public sealed record ShareLinkMintResponse(int Id, string Url, DateTimeOffset ExpiresAt);

/// <summary>
/// One row of GET /api/sharelinks. Carries no token and no hash — the list endpoint is structurally
/// incapable of returning a credential. <c>IsActive</c> is derived (not revoked and not expired) so the
/// UI never re-implements the liveness rule.
///
/// <c>OpenCount</c> / <c>LastOpenedAt</c> are the leak signal: how often the page behind THIS link was
/// opened, and when last. Nothing identifies the viewer — see <see cref="ShareLink.OpenCount"/> for why
/// that is a deliberate limit rather than a missing feature. The resource's lifetime total lives on
/// <see cref="ShareLinkListResponse.TotalOpens"/>, not here.
/// </summary>
public sealed record ShareLinkSummaryDto(
    int Id,
    string Kind,
    int ResourceId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt,
    bool IsActive,
    int OpenCount,
    DateTimeOffset? LastOpenedAt);

/// <summary>
/// Response of GET /api/sharelinks.
///
/// <c>TotalOpens</c> is the RESOURCE's lifetime open count — every link it ever had, including revoked,
/// expired and ones this response does not list (ended links are capped, see
/// <c>ShareLinkService.MaxEndedLinksListed</c>). It is the number the card badge shows, carried here so the
/// dialog cannot contradict the card: summing <c>Links[].OpenCount</c> would under-count on both axes.
/// </summary>
public sealed record ShareLinkListResponse(IReadOnlyList<ShareLinkSummaryDto> Links, int TotalOpens);

/// <summary>
/// One row of GET /api/sharelinks/mine — a link that is LIVE right now, named by the thing it publishes.
/// Carries no token and no hash, like every other list shape here.
///
/// <c>ResourceName</c> is denormalised on purpose: the whole point of the inventory is to answer "what is
/// published?" without a second lookup per row, and the id alone answers nothing a human can act on.
/// Falls back to the id when the resource has no name of its own.
/// </summary>
public sealed record ShareLinkInventoryItemDto(
    int Id,
    string Kind,
    int ResourceId,
    string ResourceName,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    int OpenCount,
    DateTimeOffset? LastOpenedAt);

/// <summary>
/// Response of GET /api/sharelinks/mine: every link of the caller's that is live right now, newest first,
/// across all four kinds.
///
/// LIVE ONLY, unlike <see cref="ShareLinkListResponse"/>. This is the safety inventory — "what can a
/// stranger open at this moment?" — so a revoked or expired link is not an answer to it, and including
/// them would bury the ones that matter. Per-resource history stays in the item's own dialog.
/// </summary>
public sealed record ShareLinkInventoryResponse(IReadOnlyList<ShareLinkInventoryItemDto> Links);

/// <summary>
/// Error body for the share endpoints — the share bounded context's own <c>{ message, code }</c>,
/// structurally identical to <see cref="ApiError"/> (D32: one uniform wire error shape). A named DTO
/// rather than an anonymous <c>new { error }</c>: an unnamed contract cannot be grepped, typed or pinned
/// by a wire-shape test. All current sites are 400s; <c>Code</c> defaults accordingly.
/// </summary>
public sealed record ShareErrorResponse(string Message, string Code = ApiErrorCodes.BadRequest);
