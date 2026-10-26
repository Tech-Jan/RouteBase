using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Heatmaps;
using RouteBaseWeb.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services.Lifecycle;

/// <inheritdoc />
public sealed class ActivityLifecycleService : IActivityLifecycleService
{
    private readonly RouteBaseDbContext _context;
    private readonly IVisibilityService _visibility;
    private readonly UserCacheInvalidator _invalidator;
    private readonly IHeatmapLifecycle _heatmaps;
    private readonly ILogger<ActivityLifecycleService> _logger;

    private readonly TimeProvider _clock;

    public ActivityLifecycleService(
        RouteBaseDbContext context, IVisibilityService visibility, UserCacheInvalidator invalidator,
        IHeatmapLifecycle heatmaps, ILogger<ActivityLifecycleService> logger,
        TimeProvider clock)
    {
        _clock = clock;
        _context     = context;
        _visibility  = visibility;
        _invalidator = invalidator;
        _heatmaps    = heatmaps;
        _logger      = logger;
    }

    /// <inheritdoc />
    public async Task<LifecycleResult> TransitionAsync(
        TrackId trackId, string to, string reason, CommitId? commitId = null, CancellationToken ct = default)
    {
        var activity = await _visibility.OwnedActivities(_context.Activities)
            .FirstOrDefaultAsync(a => a.TrackId == trackId, ct);
        return activity is null
            ? LifecycleResult.Fail("notFound")
            : await ApplyAsync(activity, to, reason, commitId, _visibility.GetCurrentUserId(),
                clearSupersededBy: false, ct);
    }

    /// <inheritdoc />
    public async Task<LifecycleResult> RestoreAsync(TrackId trackId, CancellationToken ct = default)
    {
        var activity = await _visibility.OwnedActivities(_context.Activities)
            .FirstOrDefaultAsync(a => a.TrackId == trackId, ct);
        if (activity is null) return LifecycleResult.Fail("notFound");
        if (activity.Lifecycle != LifecycleStates.Trashed) return LifecycleResult.Fail("notTrashed");

        var (target, reason) = RestorePlan(activity);
        // clearSupersededBy rides the SAME commit as the transition (like the batch path always did): a
        // failure between two separate saves left a restored, released ride still marked merged-away.
        return await ApplyAsync(
            activity, target, reason, commitId: null, _visibility.GetCurrentUserId(),
            clearSupersededBy: true, ct);
    }

    // ── Batch seam (perf P7): one load, one save, one stale-mark ────────────

    /// <inheritdoc cref="IActivityLifecycleService.TransitionManyAsync" />
    public async Task<BatchLifecycleResult> TransitionManyAsync(
        IReadOnlyList<TrackId> trackIds, string to, string reason, CancellationToken ct = default)
    {
        var activities = await _visibility.OwnedActivities(_context.Activities)
            .Where(a => trackIds.Contains(a.TrackId)).ToListAsync(ct);
        return await ApplyManyAsync(activities, _ => (to, reason), clearSupersededBy: false, ct);
    }

    /// <inheritdoc cref="IActivityLifecycleService.RestoreManyAsync" />
    public async Task<BatchLifecycleResult> RestoreManyAsync(
        IReadOnlyList<TrackId> trackIds, CancellationToken ct = default)
    {
        var trashed = await _visibility.OwnedActivities(_context.Activities)
            .Where(a => trackIds.Contains(a.TrackId) && a.Lifecycle == LifecycleStates.Trashed)
            .ToListAsync(ct);
        return await ApplyManyAsync(trashed, RestorePlan, clearSupersededBy: true, ct);
    }

    /// <summary>
    /// Where a restore goes and why. A row that predates the lifecycle axis (or one whose prev was somehow
    /// lost) restores to released — the state that lets the user see it again and decide for themselves,
    /// which is the safe direction to be wrong in.
    ///
    /// Restoring a ride the review queue rejected returns it to the review queue ("awaiting review");
    /// anything else landing in in-work is the user's own doing. This reads why THIS trash happened
    /// (the reason already recorded on the row, via the same TransitionAsync seam every trash goes
    /// through) rather than Source — a recording-owned column lifecycle has no business inspecting
    /// (arch finding B). A ride the user deliberately parked-then-deleted must not resurface in the
    /// review queue just because it happens to be auto-recorded.
    /// </summary>
    private static (string to, string reason) RestorePlan(Activity activity)
    {
        string target = LifecycleStates.IsValid(activity.LifecyclePrev)
            ? activity.LifecyclePrev
            : LifecycleStates.Released;
        string reason = target == LifecycleStates.InWork && activity.LifecycleReason == LifecycleReasons.ReviewRejected
            ? LifecycleReasons.AwaitingReview
            : LifecycleReasons.User;
        return (target, reason);
    }

    /// <summary>
    /// The single write. Validate → move the columns → append the log row → fire the side-effects of the
    /// ride entering or leaving circulation. Columns and log commit together: a state that moved without a
    /// log row would be exactly the "why did my ride vanish?" hole this axis exists to close.
    /// </summary>
    private async Task<LifecycleResult> ApplyAsync(
        Activity activity, string to, string reason, CommitId? commitId, string? userId,
        bool clearSupersededBy, CancellationToken ct)
    {
        string from = activity.Lifecycle;

        string? invalid = ApplyCore(activity, to, reason, commitId, userId, _clock.GetUtcNow().UtcDateTime);
        if (invalid is not null) return LifecycleResult.Fail(invalid);

        if (clearSupersededBy)
            activity.SupersededBy = null; // no longer merged into anything — commits WITH the transition

        await _context.SaveChangesAsync(ct);

        // Circulation changed → everything derived from "which rides count" is now wrong.
        if (from == LifecycleStates.Released || to == LifecycleStates.Released)
            await LeftOrEnteredCirculationAsync(activity, ct);

        _logger.LogInformation(
            "Activity {TrackId}: {From} → {To} ({Reason})", activity.TrackId, from, to, reason);
        return LifecycleResult.Moved(to);
    }

    /// <summary>
    /// The batch write: ApplyCore per item (each moved row gets its OWN audit row — the batch seam never
    /// thins the log), then ONE save committing all columns + log rows together, then the circulation
    /// side-effects grouped: one cache eviction per owner, one stale-mark statement for every crossing
    /// ride. Items that fail validation are skipped, exactly like their single-item calls would fail.
    /// </summary>
    private async Task<BatchLifecycleResult> ApplyManyAsync(
        List<Activity> activities, Func<Activity, (string to, string reason)> plan,
        bool clearSupersededBy, CancellationToken ct)
    {
        if (activities.Count == 0) return BatchLifecycleResult.Empty;

        string? userId = _visibility.GetCurrentUserId();
        var now = _clock.GetUtcNow().UtcDateTime;
        var moved = new List<Activity>();
        var crossed = new List<Activity>(); // released-boundary crossers, judged BEFORE mutation

        foreach (var activity in activities)
        {
            string from = activity.Lifecycle;
            var (to, reason) = plan(activity);
            if (ApplyCore(activity, to, reason, commitId: null, userId, now) is not null)
                continue; // invalid for this item — skip it, move the rest
            if (clearSupersededBy)
                activity.SupersededBy = null; // no longer merged into anything
            moved.Add(activity);
            if (from == LifecycleStates.Released || to == LifecycleStates.Released)
                crossed.Add(activity);
        }
        if (moved.Count == 0) return BatchLifecycleResult.Empty;

        await _context.SaveChangesAsync(ct);

        if (crossed.Count > 0)
        {
            foreach (var owner in crossed.Select(a => a.UserId).OfType<string>().Distinct(StringComparer.Ordinal))
                _invalidator.InvalidateUser(owner);
            await _heatmaps.MarkStaleForTracksAsync(crossed.Select(a => a.TrackId).ToList(), ct);
        }

        _logger.LogInformation("Batch lifecycle: {Moved} of {Requested} moved", moved.Count, activities.Count);
        return new BatchLifecycleResult(moved.Select(a => a.TrackId).ToList());
    }

    /// <summary>Validate + move the columns + append the audit row. No save — the caller commits
    /// (single: per item; batch: once for the whole set) and fires the circulation side-effects.</summary>
    private string? ApplyCore(Activity activity, string to, string reason, CommitId? commitId, string? userId, DateTime now)
    {
        string from = activity.Lifecycle;

        string? invalid = LifecycleTransitions.Validate(from, to, reason);
        if (invalid is not null) return invalid;

        activity.LifecyclePrev   = from;
        activity.Lifecycle       = to;
        activity.LifecycleReason = reason;
        activity.LifecycleAt     = now;

        _context.ActivityLifecycleLogs.Add(new ActivityLifecycleLog
        {
            TrackId = activity.TrackId, UserId = userId,
            FromState = from, ToState = to, Reason = reason, CommitId = commitId, At = now,
        });
        return null;
    }

    /// <summary>
    /// Side-effects of a ride entering or leaving circulation (invariant 5), fired on EVERY transition across
    /// the released boundary so no caller can forget one:
    ///
    /// - the owner's cached slices/coords go, so lists, maps and stats stop (or start) showing it at once;
    /// - every heatmap this ride has efforts on goes stale. Effort rows survive a trash — only
    ///   <c>VisibleActivities</c> filters them — but heatmap tiles are PAINTED, so a trashed ride's heat
    ///   would sit in them until something else happened to trigger a regeneration.
    /// </summary>
    private async Task LeftOrEnteredCirculationAsync(Activity activity, CancellationToken ct)
    {
        if (activity.UserId is not null)
            _invalidator.InvalidateUser(activity.UserId);

        // One statement, whatever the ride touches — which segments those are is the Heatmaps module's
        // business, not the lifecycle axis's.
        await _heatmaps.MarkStaleForTrackAsync(activity.TrackId, ct);
    }
}
