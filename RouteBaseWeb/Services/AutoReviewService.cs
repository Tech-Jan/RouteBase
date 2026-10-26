using RouteBaseWeb.Constants;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Services.TrackEdit;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services;

/// <inheritdoc />
public class AutoReviewService : IAutoReviewService
{
    private readonly RouteBaseDbContext _context;
    private readonly IVisibilityService _visibility;
    private readonly IActivityLifecycleService _lifecycle;
    private readonly IActivityCommandService _activities;
    private readonly ITrackDeriveQueue _derives;
    private readonly IProfileSettingsService _settings;
    private readonly ILogger<AutoReviewService> _logger;
    private readonly TimeProvider _clock;

    public AutoReviewService(
        RouteBaseDbContext context, IVisibilityService visibility,
        IActivityLifecycleService lifecycle, IActivityCommandService activities,
        ITrackDeriveQueue derives, IProfileSettingsService settings,
        ILogger<AutoReviewService> logger, TimeProvider clock)
    {
        _context     = context;
        _visibility  = visibility;
        _lifecycle   = lifecycle;
        _activities  = activities;
        _derives     = derives;
        _settings    = settings;
        _logger      = logger;
        _clock       = clock;
    }

    // The review queue = auto rides still in-work FOR REVIEW. The reason is load-bearing here and only here:
    // in-work also covers a ride the user parked, which is not awaiting anything and must not be listed as
    // reviewable. That is selection, not a visibility gate — invariant 2 is intact.
    // OwnedActivities has no lifecycle filter (unlike VisibleActivities), so it can see non-released rows.
    private IQueryable<Activity> PendingQuery() =>
        _visibility.OwnedActivities(_context.Activities)
            .Where(a => a.Source == ActivitySources.AutoRecording
                     && a.Lifecycle == LifecycleStates.InWork
                     && a.LifecycleReason == LifecycleReasons.AwaitingReview);

    /// <inheritdoc />
    public async Task<List<Activity>> GetReviewQueueAsync()
    {
        // Same owner scope and same two states the Record page's review controls act on — pending here,
        // and the rejected ones that Restore can bring back. OwnedActivities has no lifecycle filter, so
        // both are readable; the state predicate below is selection, not a visibility gate (invariant 2).
        // The trashed branch is reason-scoped to the review flow's own rejects: a dashboard delete
        // (reason 'user') or a merge source ('edit-merge') is never auto-purged and would sit in the
        // review panel forever — and Restore would send it to lifecycle_prev, not back to pending.
        return await _visibility.OwnedActivities(_context.Activities)
            .AsNoTracking()
            .Where(a => a.Source == ActivitySources.AutoRecording
                     && ((a.Lifecycle == LifecycleStates.InWork
                          && a.LifecycleReason == LifecycleReasons.AwaitingReview)
                         || (a.Lifecycle == LifecycleStates.Trashed
                             && (a.LifecycleReason == LifecycleReasons.ReviewRejected
                                 || a.LifecycleReason == LifecycleReasons.Abandoned))))
            .OrderByDescending(a => a.StartDateTimeLocal)
            .ToListAsync();
    }

    /// <inheritdoc />
    public async Task<bool> ConfirmAsync(TrackId trackId, string? activityType)
    {
        var activity = await PendingQuery().FirstOrDefaultAsync(a => a.TrackId == trackId);
        if (activity == null) return false;

        // Routed through the audited command service (arch finding D) rather than a raw EF write —
        // same tracked entity (identity map), so `activity.ActivityType` below still reads the update.
        if (!string.IsNullOrWhiteSpace(activityType))
            await _activities.UpdateActivityAsync(trackId, name: null, activityType: activityType, visibility: null);

        // Into circulation. The lifecycle service owns the column write, the audit row, and the side-effects
        // (cache eviction, heatmap staleness) — approving a ride is exactly "it starts counting", and this is
        // the one place that knows everything that means.
        var result = await _lifecycle.TransitionAsync(trackId, LifecycleStates.Released, LifecycleReasons.User);
        if (!result.Ok) return false;

        // Now — not at finalize. Approve is the moment an auto-detected ride enters circulation, so it is the
        // moment its segment efforts may exist (heatmap generation filters efforts by user, not by lifecycle,
        // so matching a still-pending ride would paint it). Geometry has been final since finalize, and the
        // bbox Seam-2 needs was written there.
        _derives.EnqueueMatchOnly([trackId.Value], _visibility.GetCurrentUserId(), DeriveReason.Recording);

        _logger.LogInformation("Confirmed auto activity {TrackId} as {Type}", trackId, activity.ActivityType);
        return true;
    }

    /// <inheritdoc />
    public async Task<int> ConfirmManyAsync(IReadOnlyList<TrackId> trackIds)
    {
        if (trackIds.Count == 0) return 0;

        // PendingQuery is owner-scoped + pending-only, so foreign / already-confirmed / unknown ids
        // simply don't match. The pending filter is stricter than the lifecycle service's owned-scope
        // (a user-parked in-work ride must not be released by bulk-confirm), so the ids it yields are
        // what the batch seam gets: one load, one save, one stale-mark for the whole set (P7).
        var pendingIds = await PendingQuery()
            .Where(a => trackIds.Contains(a.TrackId)).Select(a => a.TrackId).ToListAsync();
        if (pendingIds.Count == 0) return 0;

        var result = await _lifecycle.TransitionManyAsync(
            pendingIds, LifecycleStates.Released, LifecycleReasons.User);

        // One queue item for the whole batch, not one per ride: MatchOnlyIds is a list, and the derive service
        // already isolates per-track failures.
        _derives.EnqueueMatchOnly(
            pendingIds.Select(id => id.Value).ToList(), _visibility.GetCurrentUserId(), DeriveReason.Recording);

        _logger.LogInformation("Bulk-confirmed {Count} auto activities", result.Moved);
        return result.Moved;
    }

    /// <inheritdoc />
    public async Task<int> TrashManyAsync(IReadOnlyList<TrackId> trackIds)
    {
        if (trackIds.Count == 0) return 0;

        // Owner-scoped + pending-only: foreign / already-actioned / unknown ids simply don't match.
        var pendingIds = await PendingQuery()
            .Where(a => trackIds.Contains(a.TrackId)).Select(a => a.TrackId).ToListAsync();
        if (pendingIds.Count == 0) return 0;

        // One transaction across the lifecycle save and the session-abandon save (SVC-05): a crash between
        // them left review-rejected trash whose sessions stayed 'finalized' — invisible to the
        // abandoned-keyed purge sweep, so those rides were retained forever. Everything below runs on the
        // same scoped DbContext (the lifecycle service opens no transaction of its own), and InWork→Trashed
        // crosses no released boundary, so no cache eviction fires inside the window either.
        await using var tx = await _context.Database.BeginTransactionAsync();

        // reason 'review-rejected' is what earns the SHORT (2-day) purge window: this ride was looked at
        // and turned down, so it needs only enough grace to undo a misclick. A ride the user deletes
        // by hand carries reason 'user' and is never auto-purged. Retention keys off reason (invariant 2).
        var result = await _lifecycle.TransitionManyAsync(
            pendingIds, LifecycleStates.Trashed, LifecycleReasons.ReviewRejected);
        if (result.Moved == 0) return 0;

        // Route the owning sessions into the abandoned-purge window — batch twin of
        // AbandonAutoSessionAsync, one query for the whole set.
        var now = _clock.GetUtcNow().UtcDateTime;
        var movedIds = result.MovedIds.ToHashSet();
        var sessions = await _context.RecordingSessions
            .Where(s => movedIds.Contains(s.TrackId)).ToListAsync();
        foreach (var session in sessions)
        {
            session.Status = RecordingSessionStatus.Abandoned;
            session.StoppedAt = now;
        }

        await _context.SaveChangesAsync();
        await tx.CommitAsync();
        _logger.LogInformation("Trashed {Count} auto activities (2-day grace before purge)", result.Moved);
        return result.Moved;
    }

    /// <inheritdoc />
    public async Task<bool> AdjudicateAfterFinalizeAsync(TrackId trackId, string? activityType)
    {
        // No HTTP user in the finalize path — the SQL's source='auto-recording' + awaiting-review guard IS
        // the scope here, not OwnedActivities. Only a ride still in the review queue can be rejected (CR4-02),
        // so a user-confirmed row is never stomped. A 0-point false start passes too: its NULL stats read as
        // 0 < floor. Raw SQL, not the lifecycle service: the guard must be the WHERE clause of the same
        // statement that writes, or the check and the write can race.
        var discarded = await _context.Database.ExecuteSqlRawAsync(Sql.ActivityRecordingSql.DiscardShortAutoSql,
            trackId.Value, MinMovingSeconds, MinAutoDistanceMeters(activityType));
        if (discarded == 0) return false;   // genuine ride, or already confirmed — nothing to route

        // Audit twin of the guarded discard (ActivityLifecycleSql): the adjudicator is a lifecycle transition
        // like any other and must leave a trace. user_id NULL = no HTTP user (the log model documents this).
#pragma warning disable CS8604, CS8625 // EF maps null params to SQL NULL
        await _context.Database.ExecuteSqlRawAsync(Sql.ActivityLifecycleSql.InsertLogSql,
            trackId.Value, null, LifecycleStates.InWork, LifecycleStates.Trashed, LifecycleReasons.ReviewRejected, null);
#pragma warning restore CS8604, CS8625

        await AbandonAutoSessionAsync(trackId, _clock.GetUtcNow().UtcDateTime);
        await _context.SaveChangesAsync();
        // Cache eviction is the CALLER's job, after its transaction commits (SA-08 / SVC-06): this method
        // always runs inside the finalizer's open write transaction, and evicting here let a concurrent
        // read re-cache the doomed pre-commit state. The return value says a discard happened.
        return true;
    }

    /// <inheritdoc />
    public async Task<bool> AutoApproveAfterFinalizeAsync(TrackId trackId, string userId)
    {
        bool enabled = await _settings.GetSettingAsync<bool>(
            SettingCategories.Recording, SettingKeys.AutoApproveRecordings, userId,
            defaultValue: ProfileSettingsDefaults.AutoApproveRecordings);
        if (!enabled) return false;

        // Same guarded-statement shape as the adjudicator, and for the same two reasons: no HTTP user in
        // the finalize path, and the check must be the WHERE clause of the statement that writes or the
        // two can race. The awaiting-review guard is also what makes the adjudicator's discard stick —
        // that row is 'trashed' by now, so it no longer matches.
        var released = await _context.Database.ExecuteSqlRawAsync(
            Sql.ActivityRecordingSql.AutoApproveSql, trackId.Value);
        if (released == 0) return false;

        // Audit twin of the guarded update (the lifecycle service owns this for every user-driven
        // transition; this path bypasses it, so it writes its own). user_id NULL = no HTTP user.
        //
        // Bypassing it also skips LeftOrEnteredCirculationAsync, which every OTHER released-boundary
        // crossing fires. Both halves are already covered here and neither is an oversight: the cache
        // eviction is the finalizer's post-commit InvalidateUser, and the heatmap stale-mark has nothing
        // to mark — a ride being finalized has no GPSinSegment rows yet, and the segment match the
        // finalizer enqueues next marks its own segments stale per segment as it writes them.
#pragma warning disable CS8604, CS8625 // EF maps null params to SQL NULL
        await _context.Database.ExecuteSqlRawAsync(Sql.ActivityLifecycleSql.InsertLogSql,
            trackId.Value, null, LifecycleStates.InWork, LifecycleStates.Released,
            LifecycleReasons.AutoApproved, null);
#pragma warning restore CS8604, CS8625

        _logger.LogInformation("Auto-approved auto activity {TrackId} at finalize", trackId);
        return true;
    }

    // Shared discard/abandon pairing (B1-S3, rule-of-three collapse): route a just-discarded
    // auto ride's owning session into the 2-day abandoned-purge window (renders as a restorable 'Trashed'
    // row until then). 'discarded' + session 'abandoned' is how a trashed review is told apart from a
    // genuinely-abandoned recording. EF-tracked (not raw SQL) so a same-context follow-up — e.g.
    // RestoreManyAsync — reads the updated status. Caller invokes it only for tracks it actually discarded,
    // and batches the SaveChanges. Imports (no session row) are a silent no-op.
    private async Task AbandonAutoSessionAsync(TrackId trackId, DateTime now)
    {
        var session = await _context.RecordingSessions.FirstOrDefaultAsync(s => s.TrackId == trackId);
        if (session is null) return;
        session.Status = RecordingSessionStatus.Abandoned;
        session.StoppedAt = now;
    }

    // Auto-review worthiness floors (user-decided, pinned): a finalized auto ride below either the moving
    // time or the distance floor is a near-certain false trigger (walk to the car, phone jostle) and is
    // discarded rather than surfaced for review. Live here with the review lifecycle, not in the finalizer.
    private const double MinMovingSeconds = 300;

    // Cycling covers ground fast, so its distance floor is higher than on-foot.
    private static double MinAutoDistanceMeters(string? activityType) => activityType switch
    {
        "Ride" => 1500,
        _      => 800,   // Run / Walk / anything else
    };

    /// <inheritdoc />
    public async Task<int> RestoreManyAsync(IReadOnlyList<TrackId> trackIds)
    {
        if (trackIds.Count == 0) return 0;

        // Only the caller's own trashed auto rows can be restored.
        var trashed = await _visibility.OwnedActivities(_context.Activities)
            .Where(a => a.Source == ActivitySources.AutoRecording && a.Lifecycle == LifecycleStates.Trashed
                && trackIds.Contains(a.TrackId))
            .ToListAsync();
        if (trashed.Count == 0) return 0;

        // Only restore tracks whose owning session actually recorded points. A 0-point discard (the
        // finalize 0-point branch abandons the session, PointCount 0) has nothing to adjudicate — flipping
        // it to 'pending' + session 'finalized' would strand it forever (the adjudicator never re-runs) and
        // make an approvable stats-less NULL ghost. Sessions already purged away are likewise excluded.
        var ids = trashed.Select(a => a.TrackId).ToList();
        var sessions = await _context.RecordingSessions
            .Where(s => ids.Contains(s.TrackId) && s.UserId == _visibility.GetCurrentUserId())
            .ToListAsync();
        var restorable = sessions.Where(s => s.PointCount > 0).Select(s => s.TrackId).ToHashSet();
        trashed = trashed.Where(a => restorable.Contains(a.TrackId)).ToList();
        if (trashed.Count == 0) return 0;

        // RestoreManyAsync returns each ride to its lifecycle_prev — in-work for these, since that is where
        // the reject took them from. No "restore to pending" special case: restore is a transition
        // (invariant 3). Batch seam (P7): one save + one stale-mark for the whole set.
        int restored = (await _lifecycle.RestoreManyAsync(trashed.Select(a => a.TrackId).ToList())).Moved;

        // Clear 'abandoned' on the owning sessions — otherwise the background purge would still reclaim
        // a just-restored row. Back to 'finalized' (the terminal state a reviewable auto ride sits in).
        foreach (var session in sessions.Where(x => restorable.Contains(x.TrackId)))
            session.Status = RecordingSessionStatus.Finalized;

        await _context.SaveChangesAsync();
        _logger.LogInformation("Restored {Count} trashed auto activities to the review queue", restored);
        return restored;
    }
}
