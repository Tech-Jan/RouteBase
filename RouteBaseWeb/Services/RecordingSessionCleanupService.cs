using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Services.Tracks;
using Microsoft.EntityFrameworkCore;

namespace RouteBaseWeb.Services;

/// <summary>
/// Background service that periodically cleans up stale recording sessions.
///
/// Rules:
/// - 'recording' with no GPS point for 12+ hours → abandoned
/// - 'paused' with no activity for 7+ days → abandoned
/// - 'stopped' for 1+ hour (finalization never completed) → retry finalization once,
///   then park at the terminal 'finalize_failed' status if it still fails (RF-02 — never abandoned,
///   which would hard-purge a real recording; the /Record list surfaces it for manual Retry/Discard)
/// - ghost shell (released + NULL stats + no session row) → finish the interrupted purge (A3)
/// </summary>
public class RecordingSessionCleanupService : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan RecordingStaleThreshold = TimeSpan.FromHours(12);
    private static readonly TimeSpan PausedStaleThreshold = TimeSpan.FromDays(7);
    private static readonly TimeSpan StoppedStaleThreshold = TimeSpan.FromHours(1);
    // Abandoned sessions (+ their orphan Activity shell + staged GPS) older than this are hard-purged,
    // so recording cruft stops accumulating forever. REVIEW-REJECTED rides get a shorter window than
    // genuinely-abandoned recordings — already looked at and turned down, they only need enough grace to undo
    // a misclick. This is the retention policy invariant 2 reserves for lifecycle_reason: a ride the USER
    // trashed by hand (reason 'user') is never auto-purged and is not matched here at all.
    private static readonly TimeSpan AbandonedPurgeThreshold = TimeSpan.FromDays(7);
    private static readonly TimeSpan TrashedPurgeThreshold = TimeSpan.FromDays(2);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly RecordingFinalizationCoordinator _finalization;
    private readonly ILogger<RecordingSessionCleanupService> _logger;
    private readonly TimeProvider _clock;

    public RecordingSessionCleanupService(
        IServiceScopeFactory scopeFactory,
        RecordingFinalizationCoordinator finalization,
        ILogger<RecordingSessionCleanupService> logger,
        TimeProvider clock)
    {
        _scopeFactory = scopeFactory;
        _finalization = finalization;
        _logger = logger;
        _clock = clock;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(CheckInterval, stoppingToken);

            try
            {
                await RetryStuckFinalizations(stoppingToken);
                await AbandonStaleSessions(stoppingToken);
                await PurgeOldAbandonedSessions(stoppingToken);
                await ReconcileGhostShells(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "RecordingSessionCleanupService encountered an error");
            }
        }
    }

    /// <summary>
    /// Sessions in 'stopped' status for longer than StoppedStaleThreshold had their finalization
    /// fail silently. Retry it once; a failure parks the session at 'finalize_failed' (dropped from the
    /// retry query) for the user to resolve manually — abandoning it would route a real recording into
    /// the 7-day hard purge (data loss), and leaving it 'stopped' would retry a broken finalize forever.
    /// </summary>
    private async Task RetryStuckFinalizations(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RouteBaseDbContext>();
        await RetryStuckFinalizationsAsync(context, _finalization, _logger, _clock.GetUtcNow().UtcDateTime, ct);
    }

    /// <summary>
    /// Retry every session stuck in 'stopped' past <see cref="StoppedStaleThreshold"/>. A finalize failure
    /// parks the session at the terminal 'finalize_failed' status (RF-02) — never 'abandoned', which would
    /// feed the 7-day hard purge and destroy a genuinely-recorded track, and never left 'stopped', which
    /// would retry a permanently-broken finalize every cycle forever. 'finalize_failed' drops out of this
    /// query, so it is retried only on explicit user action (the /Record recent-sessions Retry button).
    /// Internal + static so it is unit-testable without a hosted service (mirrors <see cref="PurgeAbandonedOlderThanAsync"/>).
    /// </summary>
    internal static async Task RetryStuckFinalizationsAsync(
        RouteBaseDbContext context, RecordingFinalizationCoordinator finalization,
        ILogger logger, DateTime now, CancellationToken ct)
    {
        var cutoff = now - StoppedStaleThreshold;

        var stuckSessions = await context.RecordingSessions
            .Where(s => s.Status == RecordingSessionStatus.Stopped && s.StoppedAt < cutoff)
            .ToListAsync(ct);

        foreach (var session in stuckSessions)
        {
            logger.LogWarning(
                "RecordingSessionCleanup: session {SessionId} stuck in 'stopped' since {StoppedAt}; retrying finalization",
                session.SessionId, session.StoppedAt);

            try
            {
                // Through the coordinator: coalesces with a concurrent client-driven stop of the same session.
                await finalization.FinalizeAsync(session.SessionId, ct);
                logger.LogInformation(
                    "RecordingSessionCleanup: finalization retry succeeded for session {SessionId}",
                    session.SessionId);
            }
            catch (OperationCanceledException) { throw; }   // host shutdown — do nothing, retry next start
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "RecordingSessionCleanup: finalization retry failed for session {SessionId}; parking as finalize_failed",
                    session.SessionId);
                // RF-02: one automatic retry, then park at the terminal 'finalize_failed' status. NOT
                // abandoned (that routes into the 7-day hard purge — a finalize failure must never destroy a
                // real recording; blob + staged GPS are kept). NOT left 'stopped' either — that retries a
                // permanently-broken finalize forever. The /Record recent-sessions list surfaces the parked
                // row with manual Retry / Discard actions. 'finalize_failed' is inert to the live lists
                // (they filter status=='recording') and drops out of this retry query.
                session.Status = RecordingSessionStatus.FinalizeFailed;
                await context.SaveChangesAsync(ct);
            }
        }
    }

    private async Task AbandonStaleSessions(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RouteBaseDbContext>();

        var now = _clock.GetUtcNow().UtcDateTime;
        var recordingCutoff = now - RecordingStaleThreshold;
        var pausedCutoff = now - PausedStaleThreshold;

        var count = await context.Database.ExecuteSqlRawAsync(@"
            UPDATE RecordingSession
            SET status = 'abandoned', stopped_at = {0}
            WHERE (
                (status = 'recording' AND COALESCE(last_point_at, started_at) < {1})
             OR (status = 'paused'    AND COALESCE(last_point_at, started_at) < {2})
            );",
            now, recordingCutoff, pausedCutoff);

        if (count > 0)
            _logger.LogInformation("RecordingSessionCleanup: abandoned {Count} stale session(s)", count);
    }

    /// <summary>
    /// Hard-purge abandoned sessions older than <see cref="AbandonedPurgeThreshold"/> — the session row,
    /// its orphan Activity shell, staged GPS, blob and bbox — so abandoned cruft never accumulates forever.
    /// An abandoned MANUAL recording that still holds staged GPS is finalized instead (SVC-02).
    /// </summary>
    private async Task PurgeOldAbandonedSessions(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RouteBaseDbContext>();
        var command = scope.ServiceProvider.GetRequiredService<IActivityCommandService>();
        var staging = scope.ServiceProvider.GetRequiredService<RecordingStagingStore>();

        var now = _clock.GetUtcNow().UtcDateTime;
        var purged = await PurgeAbandonedOlderThanAsync(
            context, command, staging, _finalization, _logger,
            now - AbandonedPurgeThreshold, now - TrashedPurgeThreshold, ct);

        if (purged > 0)
            _logger.LogInformation(
                "RecordingSessionCleanup: purged {Count} abandoned/trashed session(s)", purged);
    }

    /// <summary>
    /// Purge abandoned sessions via the user-independent delete core, falling back to a direct session-row
    /// delete if the Activity shell is already gone. A rejected review (trashed + reason 'review-rejected')
    /// is purged once stopped before <paramref name="trashedCutoff"/>; any other abandoned session once
    /// stopped before <paramref name="abandonedCutoff"/>. Returns the count purged. An abandoned MANUAL
    /// recording that still holds staged GPS is FINALIZED instead of purged (SVC-02) — it does not count
    /// toward the return. Internal + static so it is unit-testable without a hosted service.
    /// </summary>
    internal static async Task<int> PurgeAbandonedOlderThanAsync(
        RouteBaseDbContext context, IActivityCommandService command,
        RecordingStagingStore stagingStore, RecordingFinalizationCoordinator finalization, ILogger logger,
        DateTime abandonedCutoff, DateTime trashedCutoff, CancellationToken ct)
    {
        // Projected (not tracked): removing the parent Activity in the core must not trigger an EF
        // client-cascade double-delete of a tracked session row. LEFT JOIN Activity for its lifecycle
        // and source — a session whose shell is already gone is treated as a plain abandoned recording.
        var candidates = await (
            from s in context.RecordingSessions
                .Where(s => s.Status == RecordingSessionStatus.Abandoned && s.StoppedAt != null)
            join a in context.Activities on s.TrackId equals a.TrackId into ga
            from a in ga.DefaultIfEmpty()
            select new
            {
                s.SessionId,
                s.TrackId,
                s.StoppedAt,
                s.UserId,
                Source = a != null ? a.Source : null,
                IsTrashed = a != null && a.Lifecycle == LifecycleStates.Trashed
                            && a.LifecycleReason == LifecycleReasons.ReviewRejected,
            }).ToListAsync(ct);

        var stale = candidates
            .Where(c => c.StoppedAt < (c.IsTrashed ? trashedCutoff : abandonedCutoff))
            .ToList();

        int purged = 0;
        foreach (var s in stale)
        {
            // SVC-02: an abandoned MANUAL recording (phone died / stop call never arrived) still holds real
            // staged GPS — finalize it into an activity instead of hard-purging that data. Auto-recording
            // sessions are false-trigger discards and keep the purge below; a manual session with no staging
            // has nothing to rescue and is purged too. A finalize failure leaves the session 'abandoned' for
            // the next sweep to retry — it must never fall through to the purge and destroy the track.
            if (s.Source == ActivitySources.LiveRecording && await stagingStore.ExistsAsync(s.TrackId, ct))
            {
                logger.LogWarning(
                    "RecordingSessionCleanup: abandoned manual session {SessionId} (track {TrackId}) has staged GPS; finalizing instead of purging",
                    s.SessionId, s.TrackId);
                try
                {
                    await finalization.FinalizeAsync(s.SessionId, ct);
                }
                catch (OperationCanceledException) { throw; }   // host shutdown — retry next sweep
                catch (Exception ex)
                {
                    logger.LogError(ex,
                        "RecordingSessionCleanup: finalize of abandoned manual session {SessionId} failed; leaving it for the next sweep",
                        s.SessionId);
                }
                continue;
            }

            // Atomically consume the still-eligible predicate: the WHERE clause and the delete are one
            // statement, so a user's Restore (AutoReviewService.RestoreManyAsync flips the session to
            // 'finalized') can only race THIS row on THIS exact condition — whichever write commits first
            // wins, with no gap for the other to land in between (SVC-05). Replaces a separate
            // read-then-act recheck, which still had such a gap between its read and the delete it guarded.
            var claimed = await context.Database.ExecuteSqlRawAsync(
                "DELETE FROM RecordingSession WHERE session_id = {0} AND status = 'abandoned';", [s.SessionId], ct);
            if (claimed == 0) continue;   // Restore won the race — leave the row and its Activity alone

            // Session row already atomically deleted above; just remove its Activity, owner-scoped (⑨).
            await command.DeleteActivityCoreAsync(s.TrackId, s.UserId);
            purged++;
        }
        return purged;
    }

    private async Task ReconcileGhostShells(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<RouteBaseDbContext>();
        var command = scope.ServiceProvider.GetRequiredService<IActivityCommandService>();

        var purged = await ReconcileGhostShellsAsync(context, command, _logger, ct);
        if (purged > 0)
            _logger.LogInformation("RecordingSessionCleanup: reconciled {Count} ghost shell(s)", purged);
    }

    /// <summary>
    /// Finish interrupted purges (A3). <see cref="PurgeAbandonedOlderThanAsync"/> deletes the session row
    /// first — that order is load-bearing (SVC-05 atomic claim) and must NOT be swapped — so a failure
    /// between the two deletes strands an Activity shell with no session row, in one of two shapes:
    /// a released, never-finalized (NULL-stats) shell from an abandoned-recording purge, or a TRASHED
    /// review-rejected shell (finalized, stats set) from a 2-day trash purge (SVC-15). The sweep iterates
    /// sessions, so nothing would ever revisit either. This reconciler matches exactly those stranded
    /// states and completes the purge. The trashed arm is safe to reclaim: a session-less trashed row is
    /// unrestorable anyway (RestoreManyAsync requires the session), and the state has no other source —
    /// DeleteAbandonedSessionAsync removes the whole shell, never just the session. Source is pinned to the
    /// recording flow's two values: a legacy import/edit row must never be auto-purged. A live start can't
    /// match — its shell + session row commit in one transaction (RS-02), so a shell without a session is
    /// never observable mid-start. Internal + static so it is unit-testable without a hosted service.
    /// </summary>
    internal static async Task<int> ReconcileGhostShellsAsync(
        RouteBaseDbContext context, IActivityCommandService command, ILogger logger, CancellationToken ct)
    {
        var ghosts = await context.Activities.AsNoTracking()
            .Where(a => (a.Source == ActivitySources.LiveRecording || a.Source == ActivitySources.AutoRecording)
                        && a.UserId != null
                        && !context.RecordingSessions.Any(s => s.TrackId == a.TrackId)
                        && ((a.Lifecycle == LifecycleStates.Released && a.TotalDistance == null)
                            || (a.Lifecycle == LifecycleStates.Trashed
                                && a.LifecycleReason == LifecycleReasons.ReviewRejected)))
            .Select(a => new { a.TrackId, a.UserId })
            .ToListAsync(ct);

        int purged = 0;
        foreach (var g in ghosts)
        {
            if (g.UserId is not { } userId) continue;   // filtered in the query; belt-and-braces here
            logger.LogWarning(
                "RecordingSessionCleanup: ghost shell track {TrackId} (released, NULL stats, no session); completing interrupted purge",
                g.TrackId);
            if (await command.DeleteActivityCoreAsync(g.TrackId, userId))
                purged++;
        }
        return purged;
    }
}
