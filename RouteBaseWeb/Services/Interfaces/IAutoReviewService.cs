using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// The auto-detected-activity review queue. Always-on detection records rides as
/// <c>source='auto-recording'</c> + lifecycle <c>in-work/awaiting-review</c>, which
/// <see cref="IVisibilityService.VisibleActivities"/> hides from every normal view. Here the owner
/// confirms a pending ride (→ a real activity, optionally retyped), trashes it (soft, restorable), or
/// restores a trashed one. Owner-scoped throughout — you only ever see and act on your own rides.
/// </summary>
public interface IAutoReviewService
{
    /// <summary>
    /// The owner's auto rides that still want a decision: awaiting review, plus the ones already rejected
    /// into the trash (restorable until the purge). Newest first. Full <see cref="Activity"/> rows, because
    /// the Record page renders them with the same card the dashboard and the trash use.
    /// </summary>
    Task<List<Activity>> GetReviewQueueAsync();

    /// <summary>Promote a pending ride to a real activity, optionally correcting its type. False if not found.</summary>
    Task<bool> ConfirmAsync(TrackId trackId, string? activityType);

    /// <summary>
    /// Bulk-confirm pending rides as-is (no retype). Owner-scoped: silently skips ids that are not the
    /// caller's own pending rows (foreign, already confirmed, or unknown). Returns the number confirmed.
    /// </summary>
    Task<int> ConfirmManyAsync(IReadOnlyList<TrackId> trackIds);

    /// <summary>
    /// Soft-trash reviewable rides: move them to trashed/review-rejected and route the owning sessions into the abandoned
    /// purge (2-day trash window; restorable via <see cref="RestoreManyAsync"/> until then). Owner-scoped;
    /// skips ids that are not the caller's own pending rows. Returns the number trashed.
    /// </summary>
    Task<int> TrashManyAsync(IReadOnlyList<TrackId> trackIds);

    /// <summary>
    /// Restore trashed auto rides (trashed + session 'abandoned') back to the review queue + 'finalized',
    /// clearing the pending purge. Owner-scoped; skips ids that are not the caller's own trashed rows.
    /// Returns the number restored.
    /// </summary>
    Task<int> RestoreManyAsync(IReadOnlyList<TrackId> trackIds);

    /// <summary>
    /// Post-finalize auto-review adjudication (B1-S3): discard a just-finalized auto ride
    /// that falls below the review-worthiness floors — 0-point false starts included, their NULL stats
    /// read as 0 &lt; floor — then route a discarded row's session into the 2-day trash window. Called from
    /// inside <c>FinalizeAsync</c>'s write transaction on the same scoped context (so it shares that
    /// ambient tx). <b>User-independent</b>: finalize runs with no HTTP user, so this must not use
    /// <c>GetCurrentUserId()</c>/<c>OwnedActivities</c> — the pending-only SQL guard is the scope instead.
    /// Returns true when a row was discarded — the CALLER then evicts the owner's cache AFTER its
    /// transaction commits (SA-08: evicting inside the open tx let a concurrent read re-cache the doomed
    /// pre-commit state).
    /// </summary>
    Task<bool> AdjudicateAfterFinalizeAsync(TrackId trackId, string? activityType);

    /// <summary>
    /// Post-finalize auto-approve: when the owner has <c>AutoApproveRecordings</c> on, release a
    /// just-finalized auto ride straight into circulation instead of parking it in the review queue.
    ///
    /// Runs immediately after <see cref="AdjudicateAfterFinalizeAsync"/> and inside the same write
    /// transaction, so the adjudicator's verdict wins: a ride it just trashed no longer satisfies the
    /// awaiting-review guard and is left alone. Same <b>user-independent</b> constraint — the guard is the
    /// scope, not <c>OwnedActivities</c> — which is why the owner is passed in rather than read from the
    /// request.
    /// </summary>
    /// <returns>True when this call released the ride; false when the setting is off or the guard did not
    /// match (already confirmed, just discarded, not an auto-recording). Reports what happened — it does
    /// NOT gate the segment match. The finalizer re-reads the stored lifecycle for that
    /// (<c>EnqueueSegmentMatchIfReleasedAsync</c>), so the enqueue stays correct however the ride reached
    /// released, including a hand-approve that raced this.</returns>
    Task<bool> AutoApproveAfterFinalizeAsync(TrackId trackId, string userId);
}
