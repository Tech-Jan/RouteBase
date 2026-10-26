using System.Globalization;
using System.Security.Claims;
using RouteBaseWeb.Auth;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.Sharing;

/// <inheritdoc cref="IShareLinkAccess" />
public sealed class ShareLinkAccess(
    RouteBaseDbContext db,
    ICurrentUserService currentUser,
    IHttpContextAccessor httpContextAccessor,
    TimeProvider clock) : IShareLinkAccess
{
    // Per-REQUEST memoisation only (this service is scoped). Cross-request caching of a grant is
    // forbidden: expiry and revocation are evaluated against the row on every request, and a cache
    // window in which a revoked link still works would make "revocable" a lie.
    private ShareGrant? _current;
    private bool _currentResolved;

    public async Task<ShareGrant?> ResolveAsync(string token, CancellationToken ct)
    {
        // Shape-check BEFORE hashing: the token is attacker-controlled and unbounded, and hashing every
        // malformed probe is free work for whoever is probing.
        if (!ShareTokens.IsWellFormed(token)) return null;

        var row = await db.ShareLinks.AsNoTracking()
            .FirstOrDefaultAsync(s => s.TokenHash == ShareTokens.Hash(token), ct);

        return ToGrant(row);
    }

    public async Task<ShareGrant?> CurrentAsync(CancellationToken ct)
    {
        if (_currentResolved) return _current;

        var user = httpContextAccessor.HttpContext?.User;
        if (user is null || !ShareLinkPrincipal.IsShare(user))
        {
            _currentResolved = true;
            return _current = null;
        }

        var tokenId = ShareLinkPrincipal.TokenId(user);
        if (tokenId is null)
        {
            _currentResolved = true;
            return _current = null;
        }

        // Re-read by PK and re-check expiry/revocation rather than trusting the claim: the ticket was
        // minted at authentication, and nothing else re-validates it.
        var row = await db.ShareLinks.AsNoTracking()
            .FirstOrDefaultAsync(s => s.ShareLinkId == tokenId.Value, ct);

        // Memoised only once the value is actually KNOWN. Setting the flag before the await would hand a
        // concurrent caller in the same scope the unassigned null (a spurious 404), and a failed read
        // would be memoised as "no grant" for the rest of the request.
        _current = ToGrant(row);
        _currentResolved = true;
        return _current;
    }

    public async Task<T?> WidenAsync<T>(
        ShareKind kind, int resourceId, Func<Task<T?>> read, CancellationToken ct)
        where T : class
    {
        var grant = await CurrentAsync(ct);

        // Authorization strictly BEFORE impersonation. Inverting these two lines is the whole bug class
        // this seam exists to prevent.
        if (grant is null || !grant.Covers(kind, resourceId)) return null;

        using var _ = currentUser.BeginUserScope(grant.OwnerUserId);
        return await read();
    }

    /// <summary>
    /// Row → grant, applying every liveness rule in one place. Null for revoked, expired, malformed
    /// (kind/FK disagree), or ownerless rows. Expiry is evaluated here, per request, against the row.
    /// </summary>
    private ShareGrant? ToGrant(ShareLink? row)
    {
        if (row is null) return null;
        if (row.RevokedAt is not null) return null;
        if (row.ExpiresAt <= clock.GetUtcNow().UtcDateTime) return null;
        if (string.IsNullOrEmpty(row.UserId)) return null;

        var resolved = row.Kind switch
        {
            ShareKindNames.Activity when row.TrackId is { } t => ((ShareKind, int)?)(ShareKind.Activity, t.Value),
            ShareKindNames.Live when row.TrackId is { } lt => (ShareKind.LiveRecording, lt.Value),
            ShareKindNames.Segment when row.SegmentId is { } s => (ShareKind.Segment, s.Value),
            ShareKindNames.Race when row.RaceId is { } r => (ShareKind.Race, r.Value),
            _ => null,   // kind and FK disagree — a malformed row grants nothing
        };
        if (resolved is null) return null;

        var (kind, resourceId) = resolved.Value;
        return new ShareGrant(row.ShareLinkId, kind, resourceId, row.UserId, row.ExpiresAt);
    }
}
