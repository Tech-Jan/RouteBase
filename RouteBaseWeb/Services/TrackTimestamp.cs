using System.Globalization;

namespace RouteBaseWeb.Services;

/// <summary>
/// The one place a track-point timestamp turns from wire text into an instant — shared by the GPX and
/// TCX parsers so the two can never disagree about what "unusable" means.
/// </summary>
public static class TrackTimestamp
{
    /// <summary>
    /// No activity predates GPS. A parsed year below this cannot be a real ride, so it is a corrupt or
    /// hostile timestamp — the value is 1900 rather than 1980 only to leave no argument about the
    /// boundary being over-eager.
    /// </summary>
    public const int MinPlausibleYear = 1900;

    /// <summary>
    /// True only for a timestamp that can be believed. Everything else — absent, unparseable, or
    /// implausibly old — is reported as MISSING, which both parsers already degrade gracefully for
    /// (points kept, time-based stats skipped, content-derived id instead of a wall-clock one).
    ///
    /// The implausible case is the one worth explaining: an offset that pushes an instant BELOW
    /// DateTime.MinValue does not fail TryParse — .NET clamps it to year 1 and reports success — so
    /// <c>0001-01-01T00:00:00+14:00</c> used to import as a year-0001 activity, while the MaxValue side
    /// (which DOES fail TryParse) correctly degraded to missing. Same input class, opposite outcomes;
    /// this makes both ends behave the same way.
    /// </summary>
    public static bool TryParseUtc(string? value, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrEmpty(value)) return false;

        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts))
            return false;

        // Zone-less <time> → assume UTC (deterministic across hosts), not server-local.
        if (ts.Kind == DateTimeKind.Unspecified)
            ts = DateTime.SpecifyKind(ts, DateTimeKind.Utc);

        var asUtc = ts.ToUniversalTime();
        if (asUtc.Year < MinPlausibleYear) return false;

        utc = asUtc;
        return true;
    }
}
