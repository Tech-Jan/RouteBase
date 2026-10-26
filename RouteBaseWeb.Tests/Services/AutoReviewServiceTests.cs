using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using RouteBaseWeb.Tests.Helpers;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Services.TrackEdit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Pins <see cref="AutoReviewService"/> bulk ops: ConfirmMany confirms only the caller's own pending
/// auto rows (ignoring foreign/confirmed/unknown ids); Trash soft-discards + abandons the session;
/// Restore returns a trashed row to pending + finalized. Owner-scoped, no-op on empty/unknown batches.
/// </summary>
public class AutoReviewServiceTests : IAsyncLifetime
{
    private const string Me = "me";
    private const string Other = "other";
    private RouteBaseDbContext _context = null!;
    private AutoReviewService _service = null!;
    /// <summary>Approve enqueues the segment match — captured so the confirm tests can assert it.</summary>
    private CapturingDeriveQueue Derives { get; } = new();
    /// <summary>Auto-approve reads AutoApproveRecordings; defaults to the production default (on).</summary>
    private TestProfileSettingsService Settings { get; } = new();

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:autoreview_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();
        foreach (var id in new[] { Me, Other })
            _context.Users.Add(new AppUser { UserId = id, DisplayName = id, Email = $"{id}@t.com", IsAdmin = false });
        await _context.SaveChangesAsync();

        var vis       = new TestVisibilityService(Me);
        var inval     = new UserCacheInvalidator();
        var lifecycle = TestLifecycle.For(_context, vis, inval);
        var command   = new ActivityCommandService(
            _context, vis, inval, NullLogger<ActivityCommandService>.Instance, lifecycle);
        _service = new AutoReviewService(
            _context, vis, lifecycle, command, Derives, Settings,
            NullLogger<AutoReviewService>.Instance, TimeProvider.System);
    }

    public Task DisposeAsync() => _context.DisposeAsync().AsTask();

    private async Task<int> AddAsync(
        TrackId trackId, string owner, string source, string lifecycle, string? reason = null)
    {
        _context.Activities.Add(new Activity
        {
            TrackId = trackId, UserId = owner, ActivityType = "Ride",
            Source = source, Lifecycle = lifecycle, LifecycleReason = reason,
        });
        await _context.SaveChangesAsync();
        return trackId.Value;
    }

    private async Task<string?> LifecycleAsync(TrackId trackId) =>
        await _context.Activities.AsNoTracking()
            .Where(a => a.TrackId == trackId).Select(a => a.Lifecycle).FirstOrDefaultAsync();

    [Fact]
    public async Task ConfirmMany_ConfirmsOnlyOwnedPending()
    {
        await AddAsync(new TrackId(1), Me, "auto-recording", LifecycleStates.InWork, LifecycleReasons.AwaitingReview);
        await AddAsync(new TrackId(2), Me, "auto-recording", LifecycleStates.Released);   // already released
        await AddAsync(new TrackId(3), Other, "auto-recording", LifecycleStates.InWork, LifecycleReasons.AwaitingReview);  // foreign

        var count = await _service.ConfirmManyAsync(new[] { new TrackId(1), new TrackId(2), new TrackId(3) });

        Assert.Equal(1, count);
        Assert.Equal(LifecycleStates.Released, await LifecycleAsync(new TrackId(1)));
        Assert.Equal(LifecycleStates.Released, await LifecycleAsync(new TrackId(2))); // unchanged
        Assert.Equal(LifecycleStates.InWork, await LifecycleAsync(new TrackId(3)));   // foreign untouched
    }

    [Fact]
    public async Task ConfirmMany_EmptyBatch_IsNoOp()
    {
        Assert.Equal(0, await _service.ConfirmManyAsync(Array.Empty<TrackId>()));
    }

    [Fact]
    public async Task ConfirmMany_UnknownIds_ReturnsZero()
    {
        Assert.Equal(0, await _service.ConfirmManyAsync(new[] { new TrackId(999) }));
    }

    // ── Approve enqueues the segment match ────────────────────────────────────────────────────────────
    // An auto-detected ride cannot be matched at finalize: it is still in-work awaiting review, and heatmap
    // generation filters efforts by visible USER, not by lifecycle, so a pending ride would be painted into
    // regenerated imagery. Approve is the moment it enters circulation, so approve is the moment it matches.

    [Fact]
    public async Task Confirm_EnqueuesMatchForTheApprovedTrack()
    {
        await AddAsync(new TrackId(1), Me, "auto-recording", LifecycleStates.InWork, LifecycleReasons.AwaitingReview);

        Assert.True(await _service.ConfirmAsync(new TrackId(1), activityType: null));

        var call = Derives.SingleMatchOnly();
        Assert.Equal(new[] { 1 }, call.TrackIds);
        Assert.Equal(Me, call.UserId);
        Assert.Equal(DeriveReason.Recording, call.Reason);
    }

    /// <summary>A ride that was never pending is not confirmed, so nothing may be queued for it either —
    /// otherwise a probe for a foreign id would schedule work against someone else's track.</summary>
    [Fact]
    public async Task Confirm_ForeignTrack_EnqueuesNothing()
    {
        await AddAsync(new TrackId(3), Other, "auto-recording", LifecycleStates.InWork, LifecycleReasons.AwaitingReview);

        Assert.False(await _service.ConfirmAsync(new TrackId(3), activityType: null));

        Assert.Empty(Derives.Calls);
    }

    /// <summary>Bulk approve costs ONE queue item carrying every confirmed id — not one per ride, and not
    /// one for the ids that were filtered out as foreign/already-released.</summary>
    [Fact]
    public async Task ConfirmMany_EnqueuesOneCallWithOnlyTheConfirmedIds()
    {
        await AddAsync(new TrackId(1), Me, "auto-recording", LifecycleStates.InWork, LifecycleReasons.AwaitingReview);
        await AddAsync(new TrackId(2), Me, "auto-recording", LifecycleStates.InWork, LifecycleReasons.AwaitingReview);
        await AddAsync(new TrackId(3), Other, "auto-recording", LifecycleStates.InWork, LifecycleReasons.AwaitingReview);

        Assert.Equal(2, await _service.ConfirmManyAsync(new[] { new TrackId(1), new TrackId(2), new TrackId(3) }));

        var call = Derives.SingleMatchOnly();
        Assert.Equal(new[] { 1, 2 }, call.TrackIds.OrderBy(i => i));
        Assert.Equal(DeriveReason.Recording, call.Reason);
    }

    /// <summary>Nothing was confirmed, so nothing is queued — a rejected batch must not churn the queue.</summary>
    [Fact]
    public async Task ConfirmMany_NothingPending_EnqueuesNothing()
    {
        Assert.Equal(0, await _service.ConfirmManyAsync(new[] { new TrackId(999) }));

        Assert.Empty(Derives.Calls);
    }

    /// <summary>Trash is the opposite of approve: the ride never enters circulation, so it must never be
    /// queued for a match. Pins that the enqueue sits on the confirm path only.</summary>
    [Fact]
    public async Task TrashMany_EnqueuesNothing()
    {
        await AddAsync(new TrackId(1), Me, "auto-recording", LifecycleStates.InWork, LifecycleReasons.AwaitingReview);
        await AddSessionAsync(new TrackId(1), Me, RecordingSessionStatus.Finalized);

        Assert.Equal(1, await _service.TrashManyAsync(new[] { new TrackId(1) }));

        Assert.Empty(Derives.Calls);
    }

    // pointCount defaults > 0: a genuinely-recorded ride. RF-05: restore requires points (0 = un-restorable).
    private async Task AddSessionAsync(TrackId trackId, string owner, string status, int pointCount = 10)
    {
        _context.RecordingSessions.Add(new RecordingSession
        {
            SessionId = $"s{trackId}", TrackId = trackId, UserId = owner,
            Status = status, ActivityType = "Ride", StartedAt = DateTime.UtcNow.AddHours(-1),
            PointCount = pointCount,
        });
        await _context.SaveChangesAsync();
    }

    private async Task<string?> SessionStatusAsync(TrackId trackId) =>
        await _context.RecordingSessions.AsNoTracking()
            .Where(s => s.TrackId == trackId).Select(s => s.Status).FirstOrDefaultAsync();

    [Fact]
    public async Task Trash_DiscardsPendingAndAbandonsSession()
    {
        await AddAsync(new TrackId(1), Me, "auto-recording", LifecycleStates.InWork, LifecycleReasons.AwaitingReview);
        await AddSessionAsync(new TrackId(1), Me, "finalized");
        await AddAsync(new TrackId(2), Other, "auto-recording", LifecycleStates.InWork, LifecycleReasons.AwaitingReview); // foreign — untouched
        await AddSessionAsync(new TrackId(2), Other, "finalized");

        var count = await _service.TrashManyAsync(new[] { new TrackId(1), new TrackId(2) });

        Assert.Equal(1, count);
        Assert.Equal(LifecycleStates.Trashed, await LifecycleAsync(new TrackId(1)));
        Assert.Equal("abandoned", await SessionStatusAsync(new TrackId(1)));
        Assert.Equal(LifecycleStates.InWork, await LifecycleAsync(new TrackId(2)));      // foreign review untouched
        Assert.Equal("finalized", await SessionStatusAsync(new TrackId(2)));  // foreign session untouched
    }

    [Fact]
    public async Task Restore_ReturnsTrashedToPendingAndClearsAbandon()
    {
        await AddAsync(new TrackId(3), Me, "auto-recording", LifecycleStates.InWork, LifecycleReasons.AwaitingReview);
        await AddSessionAsync(new TrackId(3), Me, "finalized");
        await _service.TrashManyAsync(new[] { new TrackId(3) });

        var count = await _service.RestoreManyAsync(new[] { new TrackId(3) });

        Assert.Equal(1, count);
        Assert.Equal(LifecycleStates.InWork, await LifecycleAsync(new TrackId(3)));
        Assert.Equal("finalized", await SessionStatusAsync(new TrackId(3))); // out of the purge window again
    }

    [Fact]
    public async Task Restore_ZeroPointSession_IsNotRestored()
    {
        // A 0-point discard (finalize 0-point branch) has nothing to adjudicate — restoring it would
        // strand an un-adjudicatable pending ghost. It stays discarded + abandoned.
        await AddAsync(new TrackId(5), Me, "auto-recording", LifecycleStates.Trashed, LifecycleReasons.ReviewRejected);
        await AddSessionAsync(new TrackId(5), Me, "abandoned", pointCount: 0);

        var count = await _service.RestoreManyAsync(new[] { new TrackId(5) });

        Assert.Equal(0, count);
        Assert.Equal(LifecycleStates.Trashed, await LifecycleAsync(new TrackId(5)));
        Assert.Equal("abandoned", await SessionStatusAsync(new TrackId(5)));
    }

    [Fact]
    public async Task Restore_NonTrashed_IsNoOp()
    {
        await AddAsync(new TrackId(4), Me, "auto-recording", LifecycleStates.InWork, LifecycleReasons.AwaitingReview); // in the queue, not trashed
        Assert.Equal(0, await _service.RestoreManyAsync(new[] { new TrackId(4) }));
        Assert.Equal(LifecycleStates.InWork, await LifecycleAsync(new TrackId(4)));
    }

    /// <summary>The queue's trashed branch is the review flow's own rejects (review-rejected/abandoned —
    /// the rows Restore can bring back to pending and the 2-day purge will reclaim). A ride the user
    /// deleted from the dashboard (reason 'user') or a merge source (reason 'edit-merge') is never purged
    /// and must not accumulate in the Record page's review panel.</summary>
    [Fact]
    public async Task ReviewQueue_TrashedBranch_ListsOnlyReviewFlowTrash()
    {
        await AddAsync(new TrackId(10), Me, "auto-recording", LifecycleStates.InWork, LifecycleReasons.AwaitingReview);
        await AddAsync(new TrackId(11), Me, "auto-recording", LifecycleStates.Trashed, LifecycleReasons.ReviewRejected);
        await AddAsync(new TrackId(12), Me, "auto-recording", LifecycleStates.Trashed, LifecycleReasons.Abandoned);
        await AddAsync(new TrackId(13), Me, "auto-recording", LifecycleStates.Trashed, LifecycleReasons.User);
        await AddAsync(new TrackId(14), Me, "auto-recording", LifecycleStates.Trashed, LifecycleReasons.EditMerge);

        var queue = await _service.GetReviewQueueAsync();

        Assert.Equal([10, 11, 12], queue.Select(a => a.TrackId.Value).Order());
    }

    /// <summary>The adjudicator's guarded discard writes its audit twin — user NULL (no HTTP user in the
    /// finalize path), exactly as the log model documents. NULL stats read as 0, below every floor.</summary>
    [Fact]
    public async Task AdjudicateAfterFinalize_Discard_LeavesALifecycleLogRow()
    {
        await AddAsync(new TrackId(6), Me, "auto-recording", LifecycleStates.InWork, LifecycleReasons.AwaitingReview);

        await _service.AdjudicateAfterFinalizeAsync(new TrackId(6), "Run");

        Assert.Equal(LifecycleStates.Trashed, await LifecycleAsync(new TrackId(6)));
        var log = Assert.Single(_context.ActivityLifecycleLogs.Where(l => l.TrackId == new TrackId(6)));
        Assert.Equal(LifecycleStates.InWork, log.FromState);
        Assert.Equal(LifecycleStates.Trashed, log.ToState);
        Assert.Equal(LifecycleReasons.ReviewRejected, log.Reason);
        Assert.Null(log.UserId);
        Assert.Null(log.CommitId);
    }

    // ── Auto-approve at finalize ──────────────────────────────────────────────────────────────────────
    // With AutoApproveRecordings on, a genuine auto ride skips the review queue entirely. The guard is the
    // WHERE clause of the statement that writes (no HTTP user in the finalize path), so these pin what it
    // must refuse as much as what it does.

    [Fact]
    public async Task AutoApproveAfterFinalize_ReleasesAPendingRide_AndAudits()
    {
        await AddAsync(new TrackId(20), Me, "auto-recording", LifecycleStates.InWork, LifecycleReasons.AwaitingReview);

        Assert.True(await _service.AutoApproveAfterFinalizeAsync(new TrackId(20), Me));

        Assert.Equal(LifecycleStates.Released, await LifecycleAsync(new TrackId(20)));
        var log = Assert.Single(_context.ActivityLifecycleLogs.Where(l => l.TrackId == new TrackId(20)));
        Assert.Equal(LifecycleStates.InWork, log.FromState);
        Assert.Equal(LifecycleStates.Released, log.ToState);
        Assert.Equal(LifecycleReasons.AutoApproved, log.Reason);
        Assert.Null(log.UserId);   // no HTTP user in the finalize path
    }

    [Fact]
    public async Task AutoApproveAfterFinalize_SettingOff_LeavesTheRideInTheReviewQueue()
    {
        Settings.Override(SettingCategories.Recording, SettingKeys.AutoApproveRecordings, false);
        await AddAsync(new TrackId(21), Me, "auto-recording", LifecycleStates.InWork, LifecycleReasons.AwaitingReview);

        Assert.False(await _service.AutoApproveAfterFinalizeAsync(new TrackId(21), Me));

        Assert.Equal(LifecycleStates.InWork, await LifecycleAsync(new TrackId(21)));
        Assert.Empty(_context.ActivityLifecycleLogs.Where(l => l.TrackId == new TrackId(21)));
    }

    /// <summary>
    /// The ordering contract with the adjudicator: auto-approve runs immediately after it, in the same
    /// transaction, so a ride it just judged a false trigger is already 'trashed' and must NOT be
    /// resurrected. The shared awaiting-review guard is what enforces that — this pins it.
    /// </summary>
    [Fact]
    public async Task AutoApproveAfterFinalize_DoesNotResurrectAnAdjudicatorDiscard()
    {
        await AddAsync(new TrackId(22), Me, "auto-recording", LifecycleStates.InWork, LifecycleReasons.AwaitingReview);
        await _service.AdjudicateAfterFinalizeAsync(new TrackId(22), "Ride");   // NULL stats → below every floor
        Assert.Equal(LifecycleStates.Trashed, await LifecycleAsync(new TrackId(22)));

        Assert.False(await _service.AutoApproveAfterFinalizeAsync(new TrackId(22), Me));

        Assert.Equal(LifecycleStates.Trashed, await LifecycleAsync(new TrackId(22)));
    }

    /// <summary>A manual live recording is released from its first second and owns no review state — the
    /// source guard must leave it (and its reason) alone.</summary>
    [Fact]
    public async Task AutoApproveAfterFinalize_IgnoresANonAutoRecording()
    {
        await AddAsync(new TrackId(23), Me, "live-recording", LifecycleStates.Released, LifecycleReasons.Recording);

        Assert.False(await _service.AutoApproveAfterFinalizeAsync(new TrackId(23), Me));

        Assert.Equal(LifecycleStates.Released, await LifecycleAsync(new TrackId(23)));
        Assert.Empty(_context.ActivityLifecycleLogs.Where(l => l.TrackId == new TrackId(23)));
    }
}
