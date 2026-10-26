using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Requests;

namespace RouteBaseWeb.Helpers;

/// <summary>
/// Boundary validation for the recording write path — the only Requests/ DTOs whose fields
/// otherwise reach the DB unchecked (MD-02). Pure/static so it is unit-testable without mocks.
/// Rejects rather than normalizes: the mobile/web clients send only valid values today, so a
/// 400 signals a real client bug.
/// </summary>
public static class RecordingRequestValidator
{
    // Values from the canonical vocabulary; membership stays CASE-SENSITIVE on purpose ("rejects rather
    // than normalizes" — a mis-cased value is a client bug, and unnormalized casing stored in the DB would
    // be invisible to VisibilityService's exact-match reads).
    private static readonly string[] AllowedVisibilities =
        [Visibilities.Private, Visibilities.Friends, Visibilities.Public];

    // Timestamp envelope (RS-03): past floor generous (offline-buffered batches can flush hours/days late);
    // future skew tight (it is what defends the 12h stale-session sweep from an immortal LastPointAt).
    private static readonly DateTime MinPlausibleUtc = new(2015, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromMinutes(10);

    /// <returns>Error message, or null when valid.</returns>
    public static string? Validate(StartRecordingRequest r) =>
        !AllowedVisibilities.Contains(r.Visibility) ? "Visibility must be 'private', 'friends', or 'public'"
        : string.IsNullOrWhiteSpace(r.TrackName) || r.TrackName.Length > 200 ? "TrackName must be 1-200 characters"
        : string.IsNullOrWhiteSpace(r.ActivityType) || r.ActivityType.Length > 50 ? "ActivityType must be 1-50 characters"
        : r.TimezoneOffsetMinutes is < -720 or > 840 ? "TimezoneOffsetMinutes must be within [-720, 840]"
        : null;

    /// <returns>Error message, or null when valid.</returns>
    public static string? Validate(RecordingPointsBatch b, DateTime nowUtc)
    {
        // Explicit loop, not Any(p => p.Lat is < -90 …): NaN/±∞ pass every range comparison (all
        // comparisons with NaN are false), so a finite check must gate first (RS-03). NaN/∞ coords would
        // otherwise poison cumulative distance and violate the NOT NULL lat column at insert (→ 500).
        var maxUtc = nowUtc + MaxFutureSkew;
        foreach (var p in b.Points)
        {
            if (!double.IsFinite(p.Lat) || p.Lat is < -90 or > 90 ||
                !double.IsFinite(p.Lng) || p.Lng is < -180 or > 180)
                return "GPS points contain out-of-range coordinates";
            if (p.Altitude is { } alt && (!double.IsFinite(alt) || alt is < -500 or > 10000))
                return "GPS points contain out-of-range altitude";
            if (p.Speed is { } spd && (!double.IsFinite(spd) || spd is < 0 or > 150))
                return "GPS points contain out-of-range speed";
            // Sea-level extremes on Earth span ~870–1085 hPa; a barometer reading outside a generous
            // 300–1100 band is a bad sensor value, not a real altitude.
            if (p.Pressure is { } pr && (!double.IsFinite(pr) || pr is < 300 or > 1100))
                return "GPS points contain out-of-range pressure";
            if (p.TimestampUtc < MinPlausibleUtc || p.TimestampUtc > maxUtc)
                return "GPS points contain implausible timestamps";
        }
        return null;
    }
}
