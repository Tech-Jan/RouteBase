namespace RouteBaseWeb.Models;

/// <summary>
/// A recent recording session joined with its Activity's review state, for the Record page's
/// "My Recent Sessions" table. Pending auto-detected rows (<see cref="IsPending"/>) render review
/// controls (approve/delete + minimap); every other row keeps today's actions. Stats come from the
/// Activity shell (NULL until finalization).
/// </summary>
public record RecentSessionView
{
    public required string SessionId { get; init; }
    public int TrackId { get; init; }

    /// <summary>recording | paused | stopped | finalized | abandoned | finalize_failed</summary>
    public required string Status { get; init; }

    /// <summary>Detected/confirmed type — the Activity's type when present, else the session's.</summary>
    public required string ActivityType { get; init; }

    public DateTime StartedAt { get; init; }
    public DateTime? StoppedAt { get; init; }

    /// <summary>StoppedAt for finished sessions, the service clock's now for running ones — the
    /// basis the Duration column renders from (K2: the partial must not read the wall clock).</summary>
    public DateTime DurationEndUtc { get; init; }

    public int PointCount { get; init; }

    /// <summary>True when this is an auto-detected recording still awaiting review
    /// (<c>source='auto-recording'</c> AND lifecycle <c>in-work/awaiting-review</c>).</summary>
    public bool IsPending { get; init; }

    /// <summary>True when this is a soft-trashed auto recording (<c>source='auto-recording' AND
    /// lifecycle='trashed'</c>) — restorable until the purge window reclaims it.</summary>
    public bool IsTrashed { get; init; }

    public double? TotalDistance { get; init; }
    public double? TimeMoving { get; init; }
    public double? TotalAscent { get; init; }
}
