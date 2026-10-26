using RouteBaseWeb.Models.Queue;

namespace RouteBaseWeb.Services.TrackEdit;

/// <summary>
/// Queue payload for the post-save/revert derive pass (CommitPath §5.1). Feature code that happens to run on
/// the queue: the queue/worker layer knows only <c>OperationType</c>/<c>OperationPayload</c>/<c>IOperationHandler</c>.
/// Buckets by what changed for the track (§14.4 — stats/bbox/start-time are now recomputed synchronously in
/// save/revert, so derive owns only the slow cross-context work: segments + photos).
/// </summary>
public sealed record TrackEditDerivePayload(
    string UserId, int? CommitId,               // null = not commit-driven (a version restore, a recording)
    IReadOnlyList<int> FullDeriveIds,   // geometry changed: replaced at save; restored at revert → remove + re-match + photos
    IReadOnlyList<int> MatchOnlyIds,    // new/unhidden blob: created at save; un-trashed at revert; a finalized/approved recording → re-match
    IReadOnlyList<int> RemoveOnlyIds,   // hidden: trashed sources at save → remove from segments
    DeriveReason Reason = DeriveReason.TrackEdit)  // labels the progress toast only — no behaviour rides on it
    : OperationPayload(UserId);
