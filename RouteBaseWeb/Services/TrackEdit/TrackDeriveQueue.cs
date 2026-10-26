namespace RouteBaseWeb.Services.TrackEdit;

/// <summary>
/// What put a derive on the queue. Carried on <see cref="TrackEditDerivePayload"/> purely so the progress
/// toast can name the work honestly: the op started life as the editor's post-save pass, but recordings and
/// elevation refresh enqueue it too, and "Recomputing edited tracks…" is wrong (and alarming) on the Record
/// page after an approve. Behaviour is identical for every value.
/// </summary>
public enum DeriveReason
{
    TrackEdit,
    Recording,
    ElevationRefresh,
}

/// <summary>The enqueue seam its callers depend on — an interface so a test can assert what was queued
/// (<see cref="OperationQueue"/> exposes only counts and the next type, never the payload).</summary>
public interface ITrackDeriveQueue
{
    void EnqueueMatchOnly(IReadOnlyList<int> trackIds, string userId, DeriveReason reason);
    void EnqueueFullDerive(IReadOnlyList<int> trackIds, string userId, DeriveReason reason);
}

/// <summary>
/// The one place that puts a <see cref="OperationQueue.OperationType.TrackEditDerive"/> item on the queue.
/// Five callers now enqueue the same shape (editor save/revert, version restore, recording finalize, review
/// approve, elevation refresh), and each hand-rolled copy had to re-remember the same two things: the queue is
/// bounded (cap 100) and <c>TryEnqueue</c> returns false on full, which silently drops the re-derive.
///
/// Dropping is tolerable — every caller is re-runnable and Analyze All is the backstop — but it must be
/// explicable, so a rejected enqueue is logged rather than swallowed.
/// </summary>
public sealed class TrackDeriveQueue(OperationQueue queue, ILogger<TrackDeriveQueue> logger) : ITrackDeriveQueue
{
    /// <summary>
    /// Geometry is unchanged, the track just needs its segment efforts (re)computed: a newly finalized or
    /// approved recording, an un-trashed track, a track whose elevation was rewritten in place. The bbox must
    /// already be upserted (Seam-2) — matching reads it to pick candidate segments.
    /// </summary>
    public void EnqueueMatchOnly(IReadOnlyList<int> trackIds, string userId, DeriveReason reason)
    {
        if (trackIds.Count == 0) return; // mirrors ImportMatchStep: nothing imported, nothing to match
        Enqueue(new TrackEditDerivePayload(userId, CommitId: null, [], trackIds, [], reason), userId, reason);
    }

    /// <summary>
    /// Geometry changed: stale matches must be removed before re-matching (a crop can shrink the bbox out of a
    /// segment that incremental matching would then never revisit), and photo positions move with the points.
    /// </summary>
    public void EnqueueFullDerive(IReadOnlyList<int> trackIds, string userId, DeriveReason reason)
    {
        if (trackIds.Count == 0) return;
        Enqueue(new TrackEditDerivePayload(userId, CommitId: null, trackIds, [], [], reason), userId, reason);
    }

    private void Enqueue(TrackEditDerivePayload payload, string userId, DeriveReason reason)
    {
        var queued = queue.TryEnqueue(new OperationQueue.WorkItem
        {
            Type = OperationQueue.OperationType.TrackEditDerive,
            UserId = userId,
            Payload = payload,
        });
        // Error, not Warning, and the level is the whole point: the Error_Log mirror
        // (ErrorLogLoggerProvider) filters at Error, so at Warning this drop never reached /Admin/Errors —
        // which is precisely why "my ride didn't join its segments" was undiagnosable after the fact. The
        // outcome it describes is error-grade even though the queue itself is behaving as designed.
        if (!queued)
            logger.LogError(
                "Derive ({Reason}) for tracks {TrackIds} was dropped: the operation queue is full. " +
                "Segment matches, efforts, heatmaps and photo positions stay stale until the next derive.",
                reason, string.Join(",", payload.FullDeriveIds.Concat(payload.MatchOnlyIds)));
    }
}
