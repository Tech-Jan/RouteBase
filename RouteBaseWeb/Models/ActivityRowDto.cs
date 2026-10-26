using RouteBaseWeb.Helpers;

namespace RouteBaseWeb.Models;

/// <summary>
/// Flat, presentation-agnostic row for the client-rendered activities grid. Carries raw values
/// (the TypeScript column formatters handle display formatting) plus the few fields that require
/// server context to resolve — the owner display name and the delete permission.
/// Units match the <see cref="Activity"/> columns: distance/ascent/descent in metres, speed in m/s,
/// times in seconds.
/// </summary>
// Mirrors ActivityRow in overview/activities-grid.ts.
public sealed class ActivityRowDto
{
    public int TrackId { get; init; }
    public string? Name { get; init; }
    public string? ActivityType { get; init; }
    [WireValues(typeof(Visibilities))]
    public string Visibility { get; init; } = Visibilities.Default;

    /// <summary>Resolved owner label ("Me" / display name), or null in single-user mode.</summary>
    public string? Owner { get; init; }

    /// <summary>Wall-clock local start (FE-12 ToWallClockIso string) — never an instant.</summary>
    public string? StartLocal { get; init; }

    public double? DistanceMeters { get; init; }
    public double? AscentMeters { get; init; }
    public double? DescentMeters { get; init; }
    public double? TimeTotalSeconds { get; init; }
    public double? TimeMovingSeconds { get; init; }
    public double? SpeedAvgMs { get; init; }
    public double? SpeedMaxMs { get; init; }
    public double? HeartRateAvg { get; init; }
    public double? HeartRateMax { get; init; }
    public double? CadenceAvg { get; init; }
    public double? PowerAvg { get; init; }

    /// <summary>Number of photos assigned to this activity. Batched separately (not on the Activity row) —
    /// see IActivityQueryService.GetPhotoCountsAsync; 0 when the activity has no photos.</summary>
    public int PhotoCount { get; init; }

    /// <summary>True when the current user may delete this activity (owns it, or it is unowned).</summary>
    public bool CanDelete { get; init; }

    /// <summary>The ride is CHECKED OUT into an open edit session — it has unsaved edits waiting on its
    /// Details page. Free: the grid already materialises Activity entities, so this is the pointer it
    /// already read (no extra query, unlike PhotoCount).</summary>
    public bool IsInEdit { get; init; }

    /// <summary>
    /// Maps an <see cref="Activity"/> to a row DTO. <paramref name="owner"/> is pre-resolved by the
    /// caller (it needs the display-name map + current user), and is null in single-user mode.
    /// <paramref name="photoCount"/> comes from the caller's batched photo-count lookup (0 if absent).
    /// </summary>
    public static ActivityRowDto From(Activity a, string? currentUserId, string? owner, int photoCount = 0) => new()
    {
        TrackId          = a.TrackId.Value,
        Name             = a.TrackName,
        ActivityType     = a.ActivityType,
        Visibility       = a.Visibility,
        Owner            = owner,
        StartLocal       = a.StartDateTimeLocal.ToWallClockIso(),
        DistanceMeters   = a.TotalDistance,
        AscentMeters     = a.TotalAscent,
        DescentMeters    = a.TotalDescent,
        TimeTotalSeconds = a.TimeTotal,
        TimeMovingSeconds = a.TimeMoving,
        SpeedAvgMs       = a.SpeedAvg,
        SpeedMaxMs       = a.SpeedMax,
        HeartRateAvg     = a.HeartRateAvg,
        HeartRateMax     = a.HeartRateMax,
        CadenceAvg       = a.CadenceAvg,
        PowerAvg         = a.PowerAvg,
        PhotoCount       = photoCount,
        CanDelete        = a.UserId == currentUserId || string.IsNullOrEmpty(a.UserId),
        IsInEdit         = a.InEditSessionId != null,
    };
}
