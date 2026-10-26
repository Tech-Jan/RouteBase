using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Requests;
using RouteBaseWeb.Models.Sync;

namespace RouteBaseWeb.Services.Interfaces;

public interface IRecordingService
{
    /// <summary>
    /// Creates a new recording session + its Activity shell (stats NULL, source = "live-recording").
    /// Returns the new session, or null if the user already has an active session.
    /// </summary>
    Task<RecordingSession?> StartSessionAsync(StartRecordingRequest request, string userId);

    /// <summary>
    /// Appends a batch of GPS points to the session.
    /// Returns the inserted GPS points (for SignalR broadcast), or null if the batch was rejected
    /// due to deduplication (clientSequence already seen) or session not found / wrong owner.
    /// </summary>
    Task<List<ExternalGpsPoint>?> AppendPointsAsync(string sessionId, RecordingPointsBatch batch, string userId);

    /// <summary>
    /// Marks the session as stopped. Returns the session (for triggering finalization),
    /// or null if not found / wrong owner.
    /// </summary>
    Task<RecordingSession?> StopSessionAsync(string sessionId, string userId);

    /// <summary>
    /// Pauses an active recording session. Points cannot be appended while paused.
    /// Returns null if not found / wrong owner / not in 'recording' status.
    /// </summary>
    Task<RecordingSession?> PauseSessionAsync(string sessionId, string userId);

    /// <summary>
    /// Resumes a paused session back to 'recording' status.
    /// Returns null if not found / wrong owner / not in 'paused' status.
    /// </summary>
    Task<RecordingSession?> ResumeSessionAsync(string sessionId, string userId);

    /// <summary>
    /// Corrects the activity type of an in-progress (recording/paused) session and its Activity shell.
    /// Used by phone auto-detect to fix an initial "Walk" once AR settles on Run/Ride.
    /// Returns null if the session is missing, not owned by the caller, already finalized, or the
    /// requested type is blank.
    /// </summary>
    Task<RecordingSession?> RetypeSessionAsync(string sessionId, string activityType, string userId);

    /// <summary>
    /// Marks a session as abandoned (discarded without finalization).
    /// Works on any status that is not already terminal (stopped/finalized/abandoned).
    /// Returns null if not found or not owned by the caller.
    /// </summary>
    Task<RecordingSession?> AbandonSessionAsync(string sessionId, string userId);

    /// <summary>Returns the caller's active (status = 'recording' or 'paused') session, or null.</summary>
    Task<RecordingSession?> GetActiveSessionAsync(string userId);

    /// <summary>Returns all active sessions visible to the given user, for live-view discovery.</summary>
    Task<List<RecordingSession>> GetActiveSessionsVisibleToAsync(List<string> visibleUserIds);

    /// <summary>Returns the session by ID, or null if not found.</summary>
    Task<RecordingSession?> GetSessionAsync(string sessionId);

    /// <summary>
    /// The (single) session for an Activity's track id, for the PUBLIC live-share surface (keyed by
    /// track id, not session id) — or null. This is the share-READ lifecycle gate the mint side promises
    /// (<c>ShareLinkService.MintAsync</c>: links CAN be minted before release, but "serve nothing until
    /// the activity is Released"): null unless the Activity shell is Released — so a trashed or
    /// awaiting-review ride serves nothing — and both shell and session belong to
    /// <paramref name="ownerUserId"/> (the grant's owner; a re-keyed session must never ride an old grant).
    /// </summary>
    Task<RecordingSession?> GetLiveShareSessionAsync(TrackId trackId, string ownerUserId, CancellationToken ct);

    /// <summary>
    /// Whether a recording session exists for <paramref name="trackId"/> owned by <paramref name="userId"/>
    /// — the live-share mint's "this track is a real recording" gate (Finding-1). Keeps that check on the
    /// recording context's own seam instead of a raw <c>db.RecordingSessions</c> read from the sharing slice,
    /// matching how the activity/segment arms of the mint switch already route through their owning services.
    /// </summary>
    Task<bool> HasSessionForTrackAsync(TrackId trackId, string userId, CancellationToken ct = default);

    /// <summary>
    /// GPS points for a live-share viewer of <paramref name="session"/>. While active, the staged tail
    /// after <paramref name="afterSeq"/> (pass -1 for the whole history), exactly as the LiveView reads it.
    /// Once finalized the tail is compacted into the blob, so EVERY request — incremental or whole-history —
    /// gets the frozen final track (downsampled, synthetic seqs). Serving it on an incremental poll too is
    /// deliberate: a watcher whose stop+finalize landed inside one poll gap must still get the finish, so
    /// the client replaces (never seq-appends) on a finalized response.
    /// </summary>
    Task<List<ExternalGpsPoint>> GetLivePointsAsync(RecordingSession session, long afterSeq);

    /// <summary>
    /// Returns the recent sessions joined with their Activity's review state (newest first), plus
    /// <b>every</b> auto-detected row still pending review — even if it falls outside the recent
    /// window — so nothing awaiting a decision is hidden by the limit. Used by the merged Record page.
    /// </summary>
    Task<List<RecentSessionView>> GetRecentSessionsWithReviewAsync(string userId, int limit = 20);

    /// <summary>
    /// Returns the session's staged GPS points with a staging <c>seq</c> strictly greater than
    /// <paramref name="afterSeq"/> (pass -1 for the whole history). Used by live viewers to catch up on
    /// load and after a reconnect. The cursor is the dense ordinal, not a timestamp: <c>ts_utc</c> is
    /// stored at whole-second precision, so a timestamp cursor skipped every remaining point of its own
    /// second (LV-01).
    /// </summary>
    Task<List<ExternalGpsPoint>> GetPointsAfterSeqAsync(string sessionId, long afterSeq);

    /// <summary>
    /// Re-arms a 'finalize_failed' session for another finalization attempt by moving it back to 'stopped'
    /// (RF-02 manual Retry). Returns false if not found, not owned by the caller, or not 'finalize_failed'.
    /// </summary>
    Task<bool> RetryFinalizeFailedAsync(string sessionId, string userId);

    /// <summary>
    /// Parks a session at the terminal 'finalize_failed' status (RF-02) — used when a manual retry re-fails.
    /// Returns false if not found or not owned by the caller.
    /// </summary>
    Task<bool> MarkFinalizeFailedAsync(string sessionId, string userId);

    /// <summary>
    /// Permanently deletes an abandoned session record (and its GPS data is left in place).
    /// Returns true if deleted, false if not found, not owned by the caller, or not in 'abandoned' status.
    /// </summary>
    Task<bool> DeleteAbandonedSessionAsync(string sessionId, string userId);
}
