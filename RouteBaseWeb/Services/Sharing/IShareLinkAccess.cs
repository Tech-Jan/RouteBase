using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Sharing;

/// <summary>
/// The share slice's authorization seam (modelled on <c>Services.Races.IRaceAccess</c>): the single place
/// a token becomes authority, and the single place that authority widens a read.
///
/// Share code must never call <c>ICurrentUserService.BeginUserScope</c> directly — go through
/// <see cref="WidenAsync"/>, which asserts the grant covers the resource FIRST.
/// </summary>
public interface IShareLinkAccess
{
    /// <summary>
    /// Plaintext token → live grant. Null for malformed, unknown, expired, revoked, or a resource that no
    /// longer exists. Called by <c>ShareLinkAuthenticationHandler</c> during authentication.
    /// </summary>
    Task<ShareGrant?> ResolveAsync(string token, CancellationToken ct);

    /// <summary>
    /// The grant behind THIS request, from the share principal's token-id claim. Null when the caller is
    /// not a share principal. Memoised per request scope only.
    /// </summary>
    Task<ShareGrant?> CurrentAsync(CancellationToken ct);

    /// <summary>
    /// The ONE widening seam. Asserts the current grant <see cref="ShareGrant.Covers"/> the resource, then
    /// runs <paramref name="read"/> with the owner's identity in scope and restores the previous identity
    /// on the way out (including if <paramref name="read"/> throws).
    ///
    /// Returns <c>null</c> when there is no grant or it does not cover — callers 404 on that, never 403.
    ///
    /// Wrap a WHOLE service call, never half of one: the widened identity must be in scope for both the
    /// visibility gate and the cache-key computation inside that call, or the two disagree.
    ///
    /// CAUTION — the read runs as the OWNER. Anything inside it that branches on
    /// <c>ICurrentUserService.GetUserId()</c> will conclude the caller IS the owner and shape its result
    /// accordingly. That is the point for visibility gates, and a leak for anything that decides what to
    /// PUT IN a DTO: it is how the AS-05 photo redaction was silently disabled, handing anonymous visitors
    /// the owner's camera model and home-directory paths. Pass such decisions in explicitly
    /// (<c>PhotoFieldScope</c> is the worked example) rather than letting them read ambient identity.
    ///
    /// <typeparamref name="T"/> is constrained to a reference type so "denied" is always a distinguishable
    /// <c>null</c>. Unconstrained, a <c>WidenAsync&lt;bool&gt;</c> would return <c>false</c> on denial —
    /// indistinguishable from a legitimate result, and the <c>is null</c> guard callers are told to write
    /// would not even compile. <paramref name="read"/> may itself return null ("found nothing"), which the
    /// caller treats identically: both are a 404.
    /// </summary>
    Task<T?> WidenAsync<T>(ShareKind kind, int resourceId, Func<Task<T?>> read, CancellationToken ct)
        where T : class;
}
