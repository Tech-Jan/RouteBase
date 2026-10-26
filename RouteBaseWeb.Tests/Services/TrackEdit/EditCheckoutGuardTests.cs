using RouteBaseWeb.Models;
using RouteBaseWeb.Services.TrackEdit;
using Xunit;

namespace RouteBaseWeb.Tests.Services.TrackEdit;

/// <summary>
/// What a CHECKED-OUT ride refuses. Revert, reset-to-original and restore all move a track's head; an open
/// edit session stamped the head it is holding and replays its script against exactly those bytes, so moving
/// it underneath would either splice the edit onto geometry the editor never saw or fail at save with a
/// stale-stamp 409 the user cannot explain. All three refuse with 409 alreadyCheckedOut while the ride is out,
/// and all three work again the moment it is discarded (the resume banner's Discard) — the same escape hatch
/// that releases the lock releases these.
///
/// (Before Activity.in_edit_session_id these operations were session-blind: the client papered over revert
/// with a beforeReload discard, which does not fire in another tab.)
/// </summary>
public class EditCheckoutGuardTests
{
    private const string Me = "me";

    private static async Task<TrackEditTestDb> RideAsync(TrackId? trackId = null, int points = 10)
    {
        var id = trackId ?? new TrackId(101);
        var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(id, Me);
        await db.SeedRampAsync(id, points);
        return db;
    }

    /// <summary>Save (a check-in), then check the ride out again, then try to revert the save.</summary>
    [Fact]
    public async Task Revert_RefusedWhileCheckedOut_AllowedAfterDiscard()
    {
        await using var db = await RideAsync();
        var svc = db.ServiceFor(Me);
        var state = (await svc.CreateAsync(new CreateSessionRequest([101]))).Response!;
        state = (await svc.AppendOpsAsync(state.SessionId, new AppendOpsRequest(1, [new CropToOp(0, 0, 4)]))).Response!;
        var saved = await db.SaveServiceFor(Me).SaveAsync(
            state.SessionId, "replace", state.SlotCounts.Select(c => new SlotCountClaim(c.SlotId, c.PointCount)).ToList());
        var commitId = new CommitId(saved.Response!.CommitId);

        // A new edit is opened on the (now edited) ride — it stamps v2's bytes.
        var reopened = (await svc.CreateAsync(new CreateSessionRequest([101]))).Response!;

        var blocked = await db.RevertServiceFor(Me).RevertAsync(commitId);
        Assert.Equal(TrackEditErrorKind.Conflict, blocked.ErrorKind);
        Assert.Equal(TrackEditApiErrors.AlreadyCheckedOut, blocked.Error!.Code);
        // The summary says the same thing, so the UI can explain the greyed-out button.
        var summary = await db.RevertServiceFor(Me).GetSummaryAsync(commitId);
        Assert.False(summary!.Revertible);
        Assert.Equal(TrackEditApiErrors.AlreadyCheckedOut, summary.BlockedBy);
        Assert.Equal(new[] { 1, 2 }, (await db.VersionsAsync(new TrackId(101))).Select(v => v.Seq)); // nothing was written

        await svc.DiscardAsync(reopened.SessionId);

        var ok = await db.RevertServiceFor(Me).RevertAsync(commitId);
        Assert.Null(ok.ErrorKind);
    }

    [Fact]
    public async Task ResetToOriginal_RefusedWhileCheckedOut_AllowedAfterDiscard()
    {
        await using var db = await RideAsync();
        var open = (await db.ServiceFor(Me).CreateAsync(new CreateSessionRequest([101]))).Response!;

        Assert.Equal(TrackEditApiErrors.AlreadyCheckedOut, await db.HistoryFor(Me).ResetToOriginalAsync(new TrackId(101)));

        await db.ServiceFor(Me).DiscardAsync(open.SessionId);
        Assert.Null(await db.HistoryFor(Me).ResetToOriginalAsync(new TrackId(101))); // already original → idempotent success
    }

    [Fact]
    public async Task Restore_RefusedWhileCheckedOut_AllowedAfterDiscard()
    {
        await using var db = await RideAsync();
        var svc = db.ServiceFor(Me);
        var state = (await svc.CreateAsync(new CreateSessionRequest([101]))).Response!;
        state = (await svc.AppendOpsAsync(state.SessionId, new AppendOpsRequest(1, [new CropToOp(0, 0, 4)]))).Response!;
        await db.SaveServiceFor(Me).SaveAsync(
            state.SessionId, "replace", state.SlotCounts.Select(c => new SlotCountClaim(c.SlotId, c.PointCount)).ToList());
        var v1 = (await db.VersionsAsync(new TrackId(101)))[0].VersionId;

        var reopened = (await svc.CreateAsync(new CreateSessionRequest([101]))).Response!;

        Assert.Equal(TrackEditApiErrors.AlreadyCheckedOut, await db.HistoryFor(Me).RestoreAsync(new TrackId(101), v1));
        Assert.Equal(2, (await db.VersionsAsync(new TrackId(101))).Count); // refused before any write — no version appended

        await svc.DiscardAsync(reopened.SessionId);

        Assert.Null(await db.HistoryFor(Me).RestoreAsync(new TrackId(101), v1));
        Assert.Equal(3, (await db.VersionsAsync(new TrackId(101))).Count); // restore appends (it does not rewrite history)
    }
}
