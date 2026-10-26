using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Lifecycle;

/// <summary>The outcome of a lifecycle move. <c>Error</c> is a code, not prose (the API maps it).</summary>
public sealed record LifecycleResult(bool Ok, string? Error = null, string? To = null)
{
    public static LifecycleResult Fail(string error) => new(false, error);
    public static LifecycleResult Moved(string to) => new(true, null, to);
}

/// <summary>Outcome of a batch move: which ids actually moved. Requested ids that were foreign,
/// missing, already there, or invalid for the transition are simply absent — the same silent-skip
/// semantics the bulk callers' owner-scoped queries have always had.</summary>
public sealed record BatchLifecycleResult(IReadOnlyList<TrackId> MovedIds)
{
    public int Moved => MovedIds.Count;
    public static readonly BatchLifecycleResult Empty = new([]);
}

/// <summary>
/// The ONLY thing that may write <c>Activity.lifecycle*</c> / <c>superseded_by</c> (invariant 5). Every
/// transition is validated against <see cref="LifecycleTransitions"/>, written with its audit-log row in one
/// transaction, and fires the side-effects of entering or leaving circulation — so a ride can never be
/// half-way in (visible in stats but not on the heatmap, or the reverse).
/// </summary>
public interface IActivityLifecycleService
{
    /// <summary>Move an owned activity to <paramref name="to"/>. Records <c>lifecycle_prev</c> so a later
    /// restore knows where to return it.</summary>
    Task<LifecycleResult> TransitionAsync(
        TrackId trackId, string to, string reason, CommitId? commitId = null, CancellationToken ct = default);

    /// <summary>
    /// Move a trashed activity back to wherever it came from (<c>lifecycle_prev</c>) and clear
    /// <c>superseded_by</c> — it is no longer merged into anything. Restore is a transition, not a lookup
    /// (invariant 3): a rejected auto ride returns to in-work, a deleted one to released.
    /// </summary>
    Task<LifecycleResult> RestoreAsync(TrackId trackId, CancellationToken ct = default);

    // ── Batch seam (perf P7) ────────────────────────────────────────────────
    // Same validation, column moves, ONE audit row per item, and released-boundary side-effects as the
    // single-item calls — but one load, one save, one stale-mark for the whole set. The defaults loop
    // the single-item seam so test fakes stay simple; ActivityLifecycleService overrides with the
    // real batch write.

    /// <summary>Batch <see cref="TransitionAsync"/> over owned activities.</summary>
    async Task<BatchLifecycleResult> TransitionManyAsync(
        IReadOnlyList<TrackId> trackIds, string to, string reason, CancellationToken ct = default)
    {
        var moved = new List<TrackId>();
        foreach (var id in trackIds)
            if ((await TransitionAsync(new TrackId(id.Value), to, reason, null, ct)).Ok)
                moved.Add(new TrackId(id.Value));
        return new BatchLifecycleResult(moved);
    }

    /// <summary>Batch <see cref="RestoreAsync"/> over owned trashed activities.</summary>
    async Task<BatchLifecycleResult> RestoreManyAsync(
        IReadOnlyList<TrackId> trackIds, CancellationToken ct = default)
    {
        var moved = new List<TrackId>();
        foreach (var id in trackIds)
            if ((await RestoreAsync(new TrackId(id.Value), ct)).Ok)
                moved.Add(new TrackId(id.Value));
        return new BatchLifecycleResult(moved);
    }
}
