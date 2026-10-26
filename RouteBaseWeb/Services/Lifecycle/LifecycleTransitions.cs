using System.Diagnostics.CodeAnalysis;
namespace RouteBaseWeb.Services.Lifecycle;

/// <summary>The three states an activity can be in. Behaviour gates on THIS and nothing else (invariant 2).</summary>
public static class LifecycleStates
{
    /// <summary>Exists, but is not in circulation: awaiting review, or deliberately parked. Hidden from
    /// lists/stats/maps/segments — everything that funnels through VisibleActivities.</summary>
    public const string InWork = "in-work";

    /// <summary>A real activity. The ONLY state that is visible and counted.</summary>
    public const string Released = "released";

    /// <summary>In the trash. Recoverable — restore returns it to <c>lifecycle_prev</c>.</summary>
    public const string Trashed = "trashed";

    public static readonly IReadOnlyList<string> All = [InWork, Released, Trashed];

    public static bool IsValid([NotNullWhen(true)] string? state) => state is InWork or Released or Trashed;
}

/// <summary>
/// WHY an activity is in its current state. Never affects visibility or counting (invariant 2) — it drives
/// retention policy and UI copy only, and which in-work rides the review queue picks up.
/// </summary>
public static class LifecycleReasons
{
    /// <summary>in-work: an auto-detected ride, finalized, waiting for the user to approve or reject it.</summary>
    public const string AwaitingReview = "awaiting-review";

    /// <summary>in-work: a live recording still in progress. (Manual recordings are released and visible while
    /// they record — this reason exists for the auto path and for a future "hold imports" mode.)</summary>
    public const string Recording = "recording";

    /// <summary>The user did it deliberately: parked it, or deleted it to the trash.</summary>
    public const string User = "user";

    /// <summary>trashed: consumed by a merge — its points were spliced into another activity.</summary>
    public const string EditMerge = "edit-merge";

    /// <summary>trashed: the review queue rejected it (or the adjudicator judged it a false trigger).
    /// This is the reason the 2-day auto-purge keys off — see RecordingSessionCleanupService.</summary>
    public const string ReviewRejected = "review-rejected";

    /// <summary>trashed: the recording was abandoned (never finished).</summary>
    public const string Abandoned = "abandoned";

    /// <summary>released: an auto-detected ride the AutoApproveRecordings setting released at finalize,
    /// with no human in the loop. Distinct from <see cref="User"/> on purpose — the audit log has to be
    /// able to say whether a person actually looked at this ride.</summary>
    public const string AutoApproved = "auto-approved";

    public static bool IsValid(string? reason) =>
        reason is AwaitingReview or Recording or User or EditMerge or ReviewRejected or Abandoned
               or AutoApproved;
}

/// <summary>
/// The lifecycle rules, pure and unit-testable — no DB, no DI. <see cref="ActivityLifecycleService"/> is the
/// only thing that may write the columns, and it asks here first.
///
/// All six ordered state pairs are legal by design, and that is not an oversight: restore has to be able to
/// return a ride to whichever state it came from (a rejected auto ride goes back to in-work, a deleted one
/// back to released), so trashed has an edge to both. The rule with teeth is not WHICH pair — it is that a
/// transition must carry a reason that MEANS something in the state it lands in (<see cref="IsReasonAllowed"/>),
/// which is what stops "trashed because it is recording" or "released because it was rejected".
/// </summary>
public static class LifecycleTransitions
{
    /// <summary>Whether <paramref name="reason"/> can explain landing in <paramref name="to"/>.</summary>
    public static bool IsReasonAllowed(string? to, string? reason) => to switch
    {
        LifecycleStates.InWork => reason is LifecycleReasons.AwaitingReview
                                          or LifecycleReasons.Recording
                                          or LifecycleReasons.User,          // parked
        LifecycleStates.Released => reason is LifecycleReasons.User            // approved, restored, released
                                            or LifecycleReasons.Recording      // a finalized live recording
                                            or LifecycleReasons.AutoApproved,  // auto-approved at finalize
        LifecycleStates.Trashed => reason is LifecycleReasons.User           // deleted
                                           or LifecycleReasons.EditMerge     // consumed by a merge
                                           or LifecycleReasons.ReviewRejected
                                           or LifecycleReasons.Abandoned,
        _ => false,
    };

    /// <summary>Null when the transition is legal; otherwise the reason it is not (an error code, not prose).</summary>
    public static string? Validate(string? from, string? to, string? reason)
    {
        if (!LifecycleStates.IsValid(from))       return "unknownFromState";
        if (!LifecycleStates.IsValid(to))         return "unknownToState";
        if (from == to)                           return "notATransition";
        if (!LifecycleReasons.IsValid(reason))    return "unknownReason";
        if (!IsReasonAllowed(to, reason))         return "reasonNotAllowedForState";
        return null;
    }
}
