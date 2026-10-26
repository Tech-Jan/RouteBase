using System.Globalization;
using System.Security.Claims;

namespace RouteBaseWeb.Auth;

/// <summary>
/// Recognising a share principal — the predicate the default-deny policies are built on.
///
/// Checks <see cref="ClaimsPrincipal.Identities"/> rather than <c>.Identity</c>: ASP.NET can merge
/// identities from several schemes onto one principal, and a share identity sitting behind a merged
/// primary must still be recognised. Reading only <c>.Identity</c> would let a merged principal slip past
/// the fallback policy's rejection.
///
/// The claim check is a second net for the same reason: an identity that carries the token-id claim is a
/// share identity whatever its AuthenticationType says.
/// </summary>
public static class ShareLinkPrincipal
{
    public static bool IsShare(ClaimsPrincipal? user) =>
        user?.Identities.Any(i =>
            i.AuthenticationType == ShareLinkDefaults.AuthenticationScheme ||
            i.HasClaim(c => c.Type == ShareLinkDefaults.TokenIdClaim)) == true;

    /// <summary>The Share_Link row id this principal was minted from, or null if absent/malformed.</summary>
    public static int? TokenId(ClaimsPrincipal user) =>
        int.TryParse(user.FindFirstValue(ShareLinkDefaults.TokenIdClaim),
                     NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            ? id
            : null;
}
