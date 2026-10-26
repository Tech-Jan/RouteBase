using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Heatmaps;

/// <summary>Outcome of <see cref="IHeatmapLifecycle.AttachAsync"/> → 404 / 409 / 202 at the API.</summary>
public enum HeatmapAttachResult { NotFound, Conflict, Attached }

/// <summary>
/// Sole owner of the <see cref="Heatmap"/> row's attach / fail / mark-stale / get-visible writes plus the
/// segment-delete tile purge (B2). A heatmap is a 1:1 optional attachment on a shapes segment; every
/// lifecycle trigger is a Segments event, but the row and the <see cref="HeatmapTilePackStore"/> handle
/// live inside the Heatmaps module so the Segments interface no longer exports another module's model.
/// The running / done / failed transitions stay in <see cref="HeatmapGenerationService"/> (same module,
/// same owner surface).
/// </summary>
public interface IHeatmapLifecycle
{
    /// <summary>
    /// Attaches (or regenerates) a <see cref="Heatmap"/> on an owned shapes segment: upserts a pending
    /// row carrying the activity-type filter, clearing prior render metadata on regeneration. Returns
    /// <see cref="HeatmapAttachResult.NotFound"/> when the segment is missing/not-owned/not-shapes and
    /// <see cref="HeatmapAttachResult.Conflict"/> while a render is pending/running. The caller enqueues
    /// the background render on <see cref="HeatmapAttachResult.Attached"/>.
    /// </summary>
    Task<HeatmapAttachResult> AttachAsync(SegmentId segmentId, IEnumerable<string> activityTypes);

    /// <summary>
    /// Demotes a still-<c>pending</c> heatmap row to <c>failed</c> with an error message. No-op when the
    /// row is missing or already running/done. Called when the background render fails to enqueue, so the
    /// heatmap isn't wedged in <c>pending</c> forever (every retry would otherwise hit the pending guard).
    /// </summary>
    Task FailPendingAsync(SegmentId segmentId, string error);

    /// <summary>
    /// Returns the <see cref="Heatmap"/> row attached to a segment visible to the current user, or null
    /// if the segment is missing, has no heatmap, or is not visible. Visibility-scoped read used by the
    /// metadata and tile endpoints.
    /// </summary>
    Task<Heatmap?> GetVisibleAsync(SegmentId segmentId, CancellationToken ct = default);

    /// <summary>
    /// Flips every attached, currently-<c>done</c> heatmap on a segment to <c>stale</c> — its tiles were
    /// painted from effort spans a re-analysis / exclusion / restore just re-shaped. Idempotent
    /// (WHERE status='done'); tiles stay viewable, regeneration is opt-in. One indexed UPDATE.
    /// The single owner of the transition that SegmentAnalysis + SegmentEfforts previously duplicated.
    /// </summary>
    Task MarkStaleAsync(SegmentId segmentId, CancellationToken ct = default);

    /// <summary>
    /// Takes a <c>done</c>/<c>stale</c> heatmap OFFLINE (→ <c>failed</c> with a curated reason) because the
    /// segment's audience just widened (SEC-01): its tiles were painted for fewer viewers and may hold rides the
    /// new ones must not see. Unlike stale, failed tiles are not served. A pending/running render re-checks the
    /// audience before it commits, so only the settled states are touched here.
    /// </summary>
    Task WithdrawAsync(SegmentId segmentId, CancellationToken ct = default);

    /// <summary>
    /// Flips every <c>done</c> heatmap on ANY segment this track has efforts on to <c>stale</c> — one indexed
    /// UPDATE, instead of a query for the segment ids plus an UPDATE per segment. The lifecycle axis calls this
    /// on every transition across the released boundary, and a bulk restore fires it once per ride, so the
    /// per-segment fan-out multiplied. Same guard and same idempotence as <see cref="MarkStaleAsync"/>.
    /// </summary>
    Task MarkStaleForTrackAsync(TrackId trackId, CancellationToken ct = default);

    /// <summary>Batch <see cref="MarkStaleForTrackAsync"/> for the lifecycle batch seam (perf P7) —
    /// HeatmapLifecycle overrides with one statement for the whole set; the default loops so test
    /// fakes stay simple.</summary>
    async Task MarkStaleForTracksAsync(IReadOnlyCollection<TrackId> trackIds, CancellationToken ct = default)
    {
        foreach (var id in trackIds) await MarkStaleForTrackAsync(id, ct);
    }

    /// <summary>
    /// Captures a segment's heatmap id before its row is cascaded away, so the delete seam can purge the
    /// filesystem tile file after commit (the DB cascade no longer frees it). Null when none attached.
    /// </summary>
    Task<int?> CaptureIdForSegmentAsync(SegmentId segmentId, CancellationToken ct = default);

    /// <summary>Delete a heatmap's packed tile file. Best-effort, idempotent; call after the row commit.</summary>
    void PurgeTiles(int heatmapId);
}
