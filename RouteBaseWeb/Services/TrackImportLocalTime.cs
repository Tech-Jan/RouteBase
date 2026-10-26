using RouteBaseWeb.Models.Sync;

namespace RouteBaseWeb.Services;

/// <summary>
/// Pure summary-mutation helper for GI-01: stamps local wall time + timezone offset from a
/// resolved UTC offset. Extracted so the offset math is unit-testable without file I/O or DI.
/// </summary>
public static class TrackImportLocalTime
{
    /// <summary>
    /// Sets <c>StartDateLocal = StartDateUtc + offset</c> and <c>TimezoneOffset</c> (hours).
    /// Caller guarantees <see cref="ExternalActivitySummary.StartDateUtc"/> has a value.
    /// </summary>
    public static void ApplyLocalTime(ExternalActivitySummary summary, TimeSpan offset)
    {
        summary.StartDateLocal = summary.StartDateUtc!.Value + offset;
        summary.TimezoneOffset = (float)offset.TotalHours;
    }
}
