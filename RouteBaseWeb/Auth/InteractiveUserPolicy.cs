namespace RouteBaseWeb.Auth;

/// <summary>
/// Authorization policy for actions only an interactive human session may perform. API-key management is
/// the canonical case: a leaked phone key must not be able to rotate itself into a fresh one, and a share
/// principal must not touch keys at all. Share-link minting is the same shape (see
/// <see cref="ShareLinkDefaults.MintPolicy"/>); both register from one builder in Program.cs.
///
/// Expressed as "authenticated, not a share principal, not an API key" rather than by naming the cookie
/// scheme: the cookie scheme does not exist in local mode (only "Local" is registered there), so
/// <c>[Authorize(AuthenticationSchemes = Cookies)]</c> throws on every call in that mode (API-03).
/// </summary>
public static class InteractiveUserPolicy
{
    public const string Name = "InteractiveUser";
}
