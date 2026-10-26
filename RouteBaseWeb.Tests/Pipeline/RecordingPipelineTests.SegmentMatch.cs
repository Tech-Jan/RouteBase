using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Services.TrackEdit;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace RouteBaseWeb.Tests.Pipeline;

/// <summary>
/// A recorded ride joins its segments automatically, the way an imported one does — the gap that used to
/// force a manual "Analyze All" after every recording.
///
/// The insertion point is lifecycle-gated, not source-gated, and these pin why that matters: a manual live
/// recording is 'released' from its first second and matches at finalize; an auto-detected ride is still
/// in-work awaiting review and must NOT match until approve; and a ride the adjudicator just discarded is
/// trashed and must never match at all. Matching a non-circulating ride would not merely be early — heatmap
/// generation filters efforts by visible USER, not by lifecycle, so it would be baked into regenerated
/// imagery. The approve half lives in AutoReviewServiceTests.
/// </summary>
public partial class RecordingPipelineTests
{
    [Fact]
    public async Task FinalizeAsync_ManualRecording_EnqueuesSegmentMatch()
    {
        var session = await _recordingService.StartSessionAsync(
            MakeStartRequest(autoDetected: false), UserId);
        Assert.NotNull(session);
        await _recordingService.AppendPointsAsync(session.SessionId, MakeBatch(MakeGpsPoints(30)), UserId);
        await _recordingService.StopSessionAsync(session.SessionId, UserId);

        await _finalizationService.FinalizeAsync(session.SessionId);

        // Released from the start, so finalize is the moment geometry AND circulation are both true.
        var activity = await _context.Activities.AsNoTracking().FirstAsync(a => a.TrackId == session.TrackId);
        Assert.Equal(LifecycleStates.Released, activity.Lifecycle);

        var call = Derives.SingleMatchOnly();
        Assert.Equal(new[] { session.TrackId.Value }, call.TrackIds);
        Assert.Equal(UserId, call.UserId);
        Assert.Equal(DeriveReason.Recording, call.Reason);
    }

    /// <summary>
    /// A genuine auto-detected ride — long enough to clear the adjudicator's floors — under the DEFAULT
    /// AutoApproveRecordings setting: released at finalize and matched immediately, no review stop.
    ///
    /// This replaces an older pin that asserted the opposite (in-work, nothing queued). That contract was
    /// deliberately retired, not violated: the review queue is now opt-in, because the adjudicator's floors
    /// already discard the false triggers it existed to catch. The off-branch below still pins the old
    /// behaviour for anyone who wants it.
    /// </summary>
    [Fact]
    public async Task FinalizeAsync_AutoRecording_AutoApproved_ReleasesAndEnqueues()
    {
        var session = await _recordingService.StartSessionAsync(
            MakeStartRequest(activityType: "Ride", autoDetected: true), UserId);
        Assert.NotNull(session);
        // 400 pts × ~4.5 m at 1 Hz ≈ 1.8 km over 400 s — clears the Ride floors (1500 m / 300 s moving).
        await _recordingService.AppendPointsAsync(session.SessionId, MakeBatch(MakeGpsPoints(400)), UserId);
        await _recordingService.StopSessionAsync(session.SessionId, UserId);

        await _finalizationService.FinalizeAsync(session.SessionId);

        var activity = await _context.Activities.AsNoTracking().FirstAsync(a => a.TrackId == session.TrackId);
        Assert.Equal(LifecycleStates.Released, activity.Lifecycle);
        Assert.Equal(LifecycleReasons.AutoApproved, activity.LifecycleReason);

        var call = Derives.SingleMatchOnly();
        Assert.Equal(new[] { session.TrackId.Value }, call.TrackIds);
        Assert.Equal(DeriveReason.Recording, call.Reason);
    }

    /// <summary>
    /// The same ride with AutoApproveRecordings OFF: it stops in the review queue and must NOT be queued for
    /// matching — it is not in circulation until the user approves it, and heatmap generation filters efforts
    /// by visible USER rather than by lifecycle, so a pending ride would be painted into regenerated imagery.
    /// </summary>
    [Fact]
    public async Task FinalizeAsync_AutoRecording_AutoApproveOff_StaysPendingAndEnqueuesNothing()
    {
        Settings.Override(SettingCategories.Recording, SettingKeys.AutoApproveRecordings, false);

        var session = await _recordingService.StartSessionAsync(
            MakeStartRequest(activityType: "Ride", autoDetected: true), UserId);
        Assert.NotNull(session);
        await _recordingService.AppendPointsAsync(session.SessionId, MakeBatch(MakeGpsPoints(400)), UserId);
        await _recordingService.StopSessionAsync(session.SessionId, UserId);

        await _finalizationService.FinalizeAsync(session.SessionId);

        var activity = await _context.Activities.AsNoTracking().FirstAsync(a => a.TrackId == session.TrackId);
        Assert.Equal(LifecycleStates.InWork, activity.Lifecycle);   // still awaiting review
        Assert.Equal(LifecycleReasons.AwaitingReview, activity.LifecycleReason);
        Assert.Empty(Derives.Calls);
    }

    /// <summary>
    /// A too-short auto ride the adjudicator discards at finalize. It is trashed, never enters circulation,
    /// and never gets an approve — so nothing may be queued for it, ever.
    /// </summary>
    [Fact]
    public async Task FinalizeAsync_DiscardedAutoRecording_EnqueuesNothing()
    {
        var session = await _recordingService.StartSessionAsync(
            MakeStartRequest(activityType: "Ride", autoDetected: true), UserId);
        Assert.NotNull(session);
        // ~90 m over 20 s — a false trigger, well under every floor.
        await _recordingService.AppendPointsAsync(session.SessionId, MakeBatch(MakeGpsPoints(20)), UserId);
        await _recordingService.StopSessionAsync(session.SessionId, UserId);

        await _finalizationService.FinalizeAsync(session.SessionId);

        var activity = await _context.Activities.AsNoTracking().FirstAsync(a => a.TrackId == session.TrackId);
        Assert.Equal(LifecycleStates.Trashed, activity.Lifecycle);
        Assert.Empty(Derives.Calls);
    }

    /// <summary>
    /// Refreshing elevation rewrites altitude, so every effort's ascent computed from the old geometry is
    /// stale. Match-only is the right shape: lat/lng are untouched, so the candidate-segment set is unchanged
    /// and the per-(track, segment) re-match just refreshes the aggregates.
    /// </summary>
    [Fact]
    public async Task RefreshElevationAsync_EnqueuesSegmentMatch()
    {
        var session = await _recordingService.StartSessionAsync(
            MakeStartRequest(autoDetected: false), UserId);
        Assert.NotNull(session);
        await _recordingService.AppendPointsAsync(session.SessionId, MakeBatch(MakeGpsPoints(30)), UserId);
        await _recordingService.StopSessionAsync(session.SessionId, UserId);
        await _finalizationService.FinalizeAsync(session.SessionId);
        Derives.Calls.Clear();   // drop the finalize enqueue — this test is about the refresh

        Assert.NotNull(await _finalizationService.RefreshElevationAsync(session.TrackId));

        var call = Derives.SingleMatchOnly();
        Assert.Equal(new[] { session.TrackId.Value }, call.TrackIds);
        Assert.Equal(DeriveReason.ElevationRefresh, call.Reason);
    }

    /// <summary>A 0-point manual session is hard-deleted at finalize (RF-06) — there is no track left to
    /// match, so the enqueue must not fire on the empty branch either.</summary>
    [Fact]
    public async Task FinalizeAsync_EmptySession_EnqueuesNothing()
    {
        var session = await _recordingService.StartSessionAsync(
            MakeStartRequest(autoDetected: false), UserId);
        Assert.NotNull(session);
        await _recordingService.StopSessionAsync(session.SessionId, UserId);

        await _finalizationService.FinalizeAsync(session.SessionId);

        Assert.Empty(Derives.Calls);
    }
}
