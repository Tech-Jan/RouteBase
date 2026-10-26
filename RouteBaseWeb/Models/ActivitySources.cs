namespace RouteBaseWeb.Models;

/// <summary>
/// The two recorded-ride values of <see cref="Activity.Source"/> (imports write other values, e.g. "gpx").
/// The literal gates review-queue eligibility, retention windows and finalize branching, so a typo would
/// silently drop rides out of those paths — C# decision sites reference these constants; tests and the raw
/// SQL in ActivityRecordingSql deliberately keep the literal, pinning the stored value.
/// </summary>
public static class ActivitySources
{
    /// <summary>Phone auto-detected recording — gated through the review queue (in-work/awaiting-review).</summary>
    public const string AutoRecording = "auto-recording";

    /// <summary>Manually started recording (Record page or phone manual start).</summary>
    public const string LiveRecording = "live-recording";
}
