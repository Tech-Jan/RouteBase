namespace RouteBaseWeb.Constants;

/// <summary>
/// The match timeout every <see cref="System.Text.RegularExpressions.Regex"/> in this project carries
/// (MA0009, adopted 2026-08-02). Without one, a pathological input makes the matcher spin forever on a
/// request thread — a denial of service that parameterisation and input escaping cannot prevent.
///
/// Only one site actually compiles a user-controlled pattern (<c>OwnerFilterResolver.BuildMatcher</c>,
/// which escapes everything but <c>*</c>/<c>?</c>); the rest are fixed literals that can never backtrack.
/// They carry the timeout anyway so the rule stays at ERROR with no exemption list — the moment one site
/// is allowed to opt out, the gate stops meaning anything.
///
/// One second is far above any legitimate match here (the longest input is a serialized OpenAPI schema)
/// and far below a request timeout, so a trip means a real pathology, never load.
/// </summary>
public static class RegexDefaults
{
    /// <summary>Milliseconds form — for <c>[GeneratedRegex]</c>, which needs a compile-time constant.</summary>
    public const int MatchTimeoutMillis = 1000;

    /// <summary><see cref="TimeSpan"/> form — for the runtime <c>Regex</c> constructors and static helpers.</summary>
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(MatchTimeoutMillis);
}
