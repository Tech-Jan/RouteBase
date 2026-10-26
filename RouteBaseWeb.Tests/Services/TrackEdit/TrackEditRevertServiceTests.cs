using RouteBaseWeb.Models;
using RouteBaseWeb.Services.TrackEdit;
using Microsoft.EntityFrameworkCore;
using Xunit;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Tests.Services.TrackEdit;

/// <summary>
/// The revert pipeline (A2 §6 + §14.4). Real save then real revert over seeded blobs: blob + stats restore,
/// source un-consume, created-activity deletion, badge retarget, chained-revert ordering, and the
/// all-verified-before-any-write atomicity (missing snapshot / superseded / already-reverted).
/// </summary>
public class TrackEditRevertServiceTests
{
    private const string Me = "me";
    private const string Other = "other";

    private static List<SlotCountClaim> Claims(SessionStateResponse s) =>
        s.SlotCounts.Select(c => new SlotCountClaim(c.SlotId, c.PointCount)).ToList();

    private static async Task<SaveResponse> SaveAsync(
        TrackEditTestDb db, IReadOnlyList<int> loadIds, string mode, params EditOp[] ops)
    {
        var svc = db.ServiceFor(Me);
        var state = (await svc.CreateAsync(new CreateSessionRequest(loadIds))).Response!;
        if (ops.Length > 0)
            state = (await svc.AppendOpsAsync(state.SessionId, new AppendOpsRequest(state.Cursor, ops))).Response!;
        var r = await db.SaveServiceFor(Me).SaveAsync(state.SessionId, mode, Claims(state));
        Assert.Null(r.ErrorKind);
        return r.Response!;
    }

    /// <summary>
    /// Un-merging under Option B: the merge products are deleted and the inputs come back. The inputs were
    /// never modified — only trashed — so restoring them is a lifecycle move, not a content restore. Their
    /// bytes were never at risk in the first place, which is the whole point of the model.
    /// </summary>
    [Fact]
    public async Task Revert_OfAMerge_DeletesTheProducts_AndBringsBothInputsBack()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        await db.AddActivityAsync(new TrackId(202), Me);
        await db.SeedRampAsync(new TrackId(202), 6);
        var original = (await db.HeadBytesAsync(new TrackId(101)))!;
        var save = await SaveAsync(db, [101, 202], "replace", new AppendOp(0, 1, TimeMode.Keep, null), new SplitAtOp(0, 5));
        var createdIds = save.Slots.Where(s => s.Created).Select(s => s.TrackId).ToList();
        Assert.Equal(2, createdIds.Count);                                     // the merged ride + the split tail

        var r = await db.RevertServiceFor(Me).RevertAsync(new CommitId(save.CommitId));

        Assert.Null(r.ErrorKind);
        foreach (int id in createdIds)
            Assert.Null(await db.ActivityAsync(new TrackId(id)));                           // every product deleted

        foreach (int input in new[] { 101, 202 })
        {
            var a = await db.ActivityAsync(new TrackId(input));
            Assert.Equal(LifecycleStates.Released, a!.Lifecycle);              // back in circulation
            Assert.Null(a.SupersededBy);                                       // nothing supersedes it any more
        }
        Assert.Equal(original, await db.HeadBytesAsync(new TrackId(101)));                  // …and its points never moved
        Assert.Null((await db.ActivityAsync(new TrackId(101)))!.EditedCommitId);            // badge cleared
        Assert.NotNull((await new TrackEditCommitStore(db.Context).GetAsync(new CommitId(save.CommitId)))!.RevertedAt);

        // Consume and restore both left their audit twins: trash + un-trash are a complete story in the log.
        foreach (int input in new[] { 101, 202 })
        {
            var states = db.Context.ActivityLifecycleLogs
                .Where(l => l.TrackId == new TrackId(input)).OrderBy(l => l.LogId).Select(l => l.ToState).ToList();
            Assert.Equal(new[] { LifecycleStates.Trashed, LifecycleStates.Released }, states, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Invariant 4: an un-merge is possible exactly while its inputs exist. Purge one — allowed, behind the
    /// "this merge can never be undone" confirmation — and the revert is BLOCKED for that true reason, rather
    /// than half-restoring a merge. Derived from the missing row; there is no revert_blocked column.
    /// </summary>
    [Fact]
    public async Task Revert_OfAMerge_WhoseInputWasPurged_IsBlockedWithInputsPurged()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        await db.AddActivityAsync(new TrackId(202), Me);
        await db.SeedRampAsync(new TrackId(202), 6);
        var save = await SaveAsync(db, [101, 202], "replace", new AppendOp(0, 1, TimeMode.Keep, null));

        await db.PurgeActivityAsync(new TrackId(202));   // the user emptied the trash, confirmation and all

        var r = await db.RevertServiceFor(Me).RevertAsync(new CommitId(save.CommitId));

        Assert.Equal(TrackEditErrorKind.Conflict, r.ErrorKind);
        Assert.Equal(TrackEditApiErrors.InputsPurged, r.Error!.Code);

        // Nothing was half-done: the merged ride is still there and 101 is still in the trash.
        int mergedId = Assert.Single(save.Slots).TrackId;
        Assert.NotNull(await db.ActivityAsync(new TrackId(mergedId)));
        Assert.Equal(LifecycleStates.Trashed, (await db.ActivityAsync(new TrackId(101)))!.Lifecycle);
    }

    [Fact]
    public async Task Revert_Twice_ReturnsAlreadyReverted()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        var save = await SaveAsync(db, [101], "replace", new CropToOp(0, 0, 4));
        var svc = db.RevertServiceFor(Me);
        await svc.RevertAsync(new CommitId(save.CommitId));

        var second = await svc.RevertAsync(new CommitId(save.CommitId));

        Assert.Equal(TrackEditApiErrors.AlreadyReverted, second.Error!.Code);
    }

    [Fact]
    public async Task Revert_Superseded_Conflicts()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        var c1 = await SaveAsync(db, [101], "replace", new CropToOp(0, 0, 8));
        var c2 = await SaveAsync(db, [101], "replace", new CropToOp(0, 0, 6)); // newer, same track

        var r = await db.RevertServiceFor(Me).RevertAsync(new CommitId(c1.CommitId));

        Assert.Equal(TrackEditApiErrors.SupersededCommit, r.Error!.Code);
        Assert.False((await new TrackEditCommitStore(db.Context).GetAsync(new CommitId(c2.CommitId)))!.RevertedAt.HasValue);
    }

    [Fact]
    public async Task Revert_ChainNewestFirst_RestoresOriginal_BadgeRetargets()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        var original = (await db.HeadBytesAsync(new TrackId(101)))!;
        var c1 = await SaveAsync(db, [101], "replace", new CropToOp(0, 0, 8));
        var afterEdit1 = (await db.HeadBytesAsync(new TrackId(101)))!;
        var c2 = await SaveAsync(db, [101], "replace", new CropToOp(0, 0, 6));
        var svc = db.RevertServiceFor(Me);

        await svc.RevertAsync(new CommitId(c2.CommitId));
        Assert.Equal(afterEdit1, await db.HeadBytesAsync(new TrackId(101)));                 // back to the state after edit 1
        Assert.Equal(new CommitId(c1.CommitId), (await db.ActivityAsync(new TrackId(101)))!.EditedCommitId); // badge retargets to c1

        await svc.RevertAsync(new CommitId(c1.CommitId));
        Assert.Equal(original, await db.HeadBytesAsync(new TrackId(101)));                    // back to the original
        Assert.Null((await db.ActivityAsync(new TrackId(101)))!.EditedCommitId);              // badge cleared
    }

    [Fact]
    public async Task Revert_CopyCommit_DeletesCreatedOnly()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        var original = (await db.HeadBytesAsync(new TrackId(101)))!;
        var save = await SaveAsync(db, [101], "copy", new CropToOp(0, 0, 4));
        int createdId = Assert.Single(save.Slots).TrackId;

        var r = await db.RevertServiceFor(Me).RevertAsync(new CommitId(save.CommitId));

        Assert.Null(r.ErrorKind);
        Assert.Equal(original, await db.HeadBytesAsync(new TrackId(101))); // original never touched
        Assert.Null(await db.ActivityAsync(new TrackId(createdId)));       // only the created copy removed
        Assert.Equal(1, r.Response!.Deleted);
    }

    [Fact]
    public async Task Revert_ForeignCommit_NotFound()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me, Other);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        var save = await SaveAsync(db, [101], "replace", new CropToOp(0, 0, 4));

        var r = await db.RevertServiceFor(Other).RevertAsync(new CommitId(save.CommitId));

        Assert.Equal(TrackEditErrorKind.NotFound, r.ErrorKind);
    }

    [Fact]
    public async Task Revert_MissingSnapshot_FailsWithoutPartialWrites()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        // A crop REPLACES 101 in place (only a merge mints a new id), so this commit has a version to go back
        // to — and this test deletes it, as a retention sweep would.
        var save = await SaveAsync(db, [101], "replace", new CropToOp(0, 0, 4));
        var editedBlob = (await db.HeadBytesAsync(new TrackId(101)))!;
        await db.Context.Database.ExecuteSqlRawAsync(
            "DELETE FROM Activity_Version WHERE track_id = 101 AND seq = 1;");

        var r = await db.RevertServiceFor(Me).RevertAsync(new CommitId(save.CommitId));

        Assert.Equal(TrackEditApiErrors.SnapshotMissing, r.Error!.Code);
        Assert.Equal(editedBlob, await db.HeadBytesAsync(new TrackId(101)));                             // nothing rolled back
        Assert.Null((await new TrackEditCommitStore(db.Context).GetAsync(new CommitId(save.CommitId)))!.RevertedAt);
    }

    [Fact]
    public async Task Revert_CreatedAlreadyUserDeleted_SkipsAndCounts()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        var save = await SaveAsync(db, [101], "copy", new CropToOp(0, 0, 4));
        int createdId = Assert.Single(save.Slots).TrackId;
        db.Context.Activities.Remove(await db.Context.Activities.FindAsync(new TrackId(createdId)) ?? new());
        await db.Context.SaveChangesAsync();

        var r = await db.RevertServiceFor(Me).RevertAsync(new CommitId(save.CommitId));

        Assert.Null(r.ErrorKind);
        Assert.Equal(0, r.Response!.Deleted);   // delete step skipped, revert still proceeded
    }

    [Fact]
    public async Task Revert_EnqueuesDerive_RestoredAsMatchOnly()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        await db.AddActivityAsync(new TrackId(202), Me);
        await db.SeedRampAsync(new TrackId(202), 6);
        var save = await SaveAsync(db, [101, 202], "replace", new AppendOp(0, 1, TimeMode.Keep, null));
        await db.DequeueOneAsync();   // drain the save's derive item

        await db.RevertServiceFor(Me).RevertAsync(new CommitId(save.CommitId));

        // Un-merging replaces no content (the inputs were only trashed), so nothing needs a full re-derive —
        // the two rides coming back out of the trash just need their segment efforts matched again.
        var payload = Assert.IsType<TrackEditDerivePayload>((await db.DequeueOneAsync()).Payload);
        Assert.Empty(payload.FullDeriveIds);
        Assert.Equal(new[] { 101, 202 }, payload.MatchOnlyIds);
        Assert.Empty(payload.RemoveOnlyIds);
    }

    [Fact]
    public async Task CommitSummary_ReportsRevertibleAndNames()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me, name: "Ride");
        await db.SeedRampAsync(new TrackId(101), 10);
        await db.AddActivityAsync(new TrackId(202), Me, name: "Loop");
        await db.SeedRampAsync(new TrackId(202), 6);
        var save = await SaveAsync(db, [101, 202], "replace", new AppendOp(0, 1, TimeMode.Keep, null));

        var summary = await db.RevertServiceFor(Me).GetSummaryAsync(new CommitId(save.CommitId));

        // A merge replaces nothing: it creates the merged ride and trashes both inputs.
        Assert.True(summary!.Revertible);
        Assert.Null(summary.BlockedBy);
        Assert.Empty(summary.Replaced);
        Assert.Equal(new[] { "Ride", "Loop" }, summary.Trashed.Select(t => t.Name), StringComparer.Ordinal);
        Assert.Single(summary.Created);
    }

    /// <summary>
    /// Two properties in one. (1) A commit's id lists are DERIVED from what the rows say, so a purged track
    /// drops OUT of the summary instead of dangling in it as "(deleted)" — exactly what a stored JSON id list
    /// could not do, and how the original bug's ghost ids survived. (2) Because it is gone, the merge is no
    /// longer revertible: un-merging means putting it back, and it is not there to put back (invariant 4).
    /// </summary>
    [Fact]
    public async Task CommitSummary_PurgedSource_DropsOutOfTheSummary_AndBlocksTheUnMerge()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me, name: "Ride");
        await db.SeedRampAsync(new TrackId(101), 10);
        await db.AddActivityAsync(new TrackId(202), Me, name: "Loop");
        await db.SeedRampAsync(new TrackId(202), 6);
        var save = await SaveAsync(db, [101, 202], "replace", new AppendOp(0, 1, TimeMode.Keep, null));

        db.Context.Activities.Remove(await db.Context.Activities.FindAsync(new TrackId(202)) ?? new()); // the source is purged
        await db.Context.SaveChangesAsync();

        var summary = await db.RevertServiceFor(Me).GetSummaryAsync(new CommitId(save.CommitId));

        // 202 simply is not in the answer any more — no dangling "(deleted)" id.
        Assert.Equal(new[] { 101 }, summary!.Trashed.Select(t => t.TrackId));
        Assert.Empty(summary.Replaced);                                 // a merge replaces nothing

        // …and the merge can never be undone, which the badge must say for the TRUE reason.
        Assert.False(summary.Revertible);
        Assert.Equal(TrackEditApiErrors.InputsPurged, summary.BlockedBy);
    }

    [Fact]
    public async Task Revert_RestoresStatsSynchronously()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        var save = await SaveAsync(db, [101], "replace", new CropToOp(0, 4, 6)); // crop to 3 pts
        double croppedDistance = (await db.ActivityAsync(new TrackId(101)))!.TotalDistance!.Value;

        await db.RevertServiceFor(Me).RevertAsync(new CommitId(save.CommitId));

        // KPIs reflect the restored full blob immediately (before any queued derive runs).
        Assert.True((await db.ActivityAsync(new TrackId(101)))!.TotalDistance > croppedDistance);
    }
}
