using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.TrackEdit;
using RouteBaseWeb.Services.Tracks;
using Microsoft.EntityFrameworkCore;
using Xunit;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Tests.Services.TrackEdit;

/// <summary>
/// The Replace / Copy save pipeline (A2 §4 + §14.4). Real stores + engine + codec + import/command path over
/// seeded blobs. Pins invariant 1 (stamp + count verification), invariant 5 (snapshot-first overwrite), the
/// splice/split/copy mappings, the synchronous in-tx stat/bbox/start recompute, and the derive enqueue.
/// </summary>
public class TrackEditSaveServiceTests
{
    private const string Me = "me";

    private static async Task<(string SessionId, List<SlotCountClaim> Claims)> SessionWithAsync(
        TrackEditTestDb db, IReadOnlyList<int> loadIds, params EditOp[] appendOps)
    {
        var svc = db.ServiceFor(Me);
        var state = (await svc.CreateAsync(new CreateSessionRequest(loadIds))).Response!;
        if (appendOps.Length > 0)
            state = (await svc.AppendOpsAsync(state.SessionId, new AppendOpsRequest(state.Cursor, appendOps))).Response!;
        var claims = state.SlotCounts.Select(c => new SlotCountClaim(c.SlotId, c.PointCount)).ToList();
        return (state.SessionId, claims);
    }

    private static async Task<DecodedTrack> DecodeAsync(TrackEditTestDb db, TrackId trackId) =>
        TrackBlobCodec.Decode((await db.HeadBytesAsync(trackId))!);

    // ---- replace ----

    [Fact]
    public async Task SaveReplace_Crop_OverwritesBlob_SnapshotFirst()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        var oldBytes = (await db.HeadBytesAsync(new TrackId(101)))!;
        var (id, claims) = await SessionWithAsync(db, [101], new CropToOp(0, 0, 4));

        var r = await db.SaveServiceFor(Me).SaveAsync(id, "replace", claims);

        Assert.Null(r.ErrorKind);
        Assert.Equal(5, (await DecodeAsync(db, new TrackId(101))).Count);
        var commit = await new TrackEditCommitStore(db.Context).GetAsync(new CommitId(r.Response!.CommitId));
        Assert.Equal("replace", commit!.SaveMode);
        Assert.Equal(new[] { new TrackId(101) }, commit.ReplacedIds);
        // The pre-edit state is not a snapshot any more — it is the version this save superseded, still
        // readable under its own hash. (The old model kept it as a child row of the activity, so deleting
        // the activity destroyed it; that is the bug the content axis removes.)
        var versions = await db.VersionsAsync(new TrackId(101));
        Assert.Equal(new[] { 1, 2 }, versions.Select(v => v.Seq));
        Assert.Equal(10, versions[0].PointCount);                                          // OLD count
        Assert.Equal(oldBytes, await db.Objects.GetAsync(versions[0].ObjectSha));          // OLD bytes, intact
        var edited = Assert.Single(await db.Content.VersionsOfCommitAsync(new CommitId(r.Response.CommitId)));
        Assert.Equal(versions[1].VersionId, edited.VersionId);                             // v2 is what the commit wrote
        Assert.Equal(edited.VersionId, await db.Content.GetHeadVersionIdAsync(new TrackId(101)));       // …and the head now points at it
    }

    /// <summary>CHECK-IN: the save releases the ride (the revision it appends IS the check-in), and the ride is
    /// immediately checkable-out again. The session row survives — its commit references it — so the pointer is
    /// cleared explicitly rather than by the FK. The version list is untouched by any of this.</summary>
    [Fact]
    public async Task SaveReplace_ChecksTheRideBackIn()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        var (id, claims) = await SessionWithAsync(db, [101], new CropToOp(0, 0, 4));
        Assert.Equal(id, (await db.ActivityAsync(new TrackId(101)))!.InEditSessionId);   // checked out by the session create

        var r = await db.SaveServiceFor(Me).SaveAsync(id, "replace", claims);

        Assert.Null(r.ErrorKind);
        Assert.Null((await db.ActivityAsync(new TrackId(101)))!.InEditSessionId);        // released
        Assert.Equal("saved", (await db.Sessions.GetAsync(id))!.Status);    // …but the session row stays
        Assert.Equal(new[] { 1, 2 }, (await db.VersionsAsync(new TrackId(101))).Select(v => v.Seq)); // content axis untouched

        var reopened = await db.ServiceFor(Me).CreateAsync(new CreateSessionRequest([101]));
        Assert.Null(reopened.ErrorKind);
    }

    [Fact]
    public async Task SaveReplace_StaleStamp_RejectsWithoutWrites()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        var (id, claims) = await SessionWithAsync(db, [101], new CropToOp(0, 0, 4));
        await db.MutateBlobAsync(new TrackId(101));   // blob changes under the session → stamp goes stale

        var r = await db.SaveServiceFor(Me).SaveAsync(id, "replace", claims);

        Assert.Equal(TrackEditErrorKind.Conflict, r.ErrorKind);
        Assert.Equal(TrackEditApiErrors.StaleTrack, r.Error!.Code);
        Assert.Empty(await db.CommitIdsForAsync(Me));
    }

    [Fact]
    public async Task SaveReplace_CountClaimMismatch_AbortsWithoutWrites()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        var (id, _) = await SessionWithAsync(db, [101], new CropToOp(0, 0, 4));

        var r = await db.SaveServiceFor(Me).SaveAsync(id, "replace", [new SlotCountClaim(0, 999)]);

        Assert.Equal(TrackEditApiErrors.ReplayMismatch, r.Error!.Code);
        Assert.Empty(await db.CommitIdsForAsync(Me));
    }

    [Fact]
    public async Task SaveReplace_ReplayError_Surfaces422WithOpIndex()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        var id = (await db.ServiceFor(Me).CreateAsync(new CreateSessionRequest([101]))).Response!.SessionId;
        // Smuggle an out-of-range op straight into the stored script (bypassing append validation).
        var session = await db.Sessions.GetAsync(id);
        await db.Sessions.UpdateScriptCursorStampsAsync(
            id, new EditScript(1, [new LoadActivityOp(101), new CropToOp(0, 0, 999)]), 2, session!.Stamps, DateTime.UtcNow);

        var r = await db.SaveServiceFor(Me).SaveAsync(id, "replace", [new SlotCountClaim(0, 5)]);

        Assert.Equal(TrackEditErrorKind.Unprocessable, r.ErrorKind);
        Assert.Equal(TrackEditErrors.RangeOutOfBounds, r.Error!.Code);
        Assert.Equal(1, r.Error.OpIndex);
        Assert.Empty(await db.CommitIdsForAsync(Me));
    }

    [Fact]
    public async Task Save_DuplicateSlotClaims_ReturnsReplayMismatch()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        var (id, _) = await SessionWithAsync(db, [101], new CropToOp(0, 0, 4)); // slot 0 → 5 pts
        var oldBytes = (await db.HeadBytesAsync(new TrackId(101)))!;

        var r = await db.SaveServiceFor(Me).SaveAsync(
            id, "replace", [new SlotCountClaim(0, 5), new SlotCountClaim(0, 5)]); // duplicate slotId

        Assert.Equal(TrackEditErrorKind.Conflict, r.ErrorKind);
        Assert.Equal(TrackEditApiErrors.ReplayMismatch, r.Error!.Code);
        Assert.Empty(await db.CommitIdsForAsync(Me));
        Assert.Equal(oldBytes, (await db.HeadBytesAsync(new TrackId(101)))!); // no writes
    }

    [Fact]
    public async Task Save_IgnoresStampOutsideEffectiveScript()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        await db.AddActivityAsync(new TrackId(202), Me);
        await db.SeedRampAsync(new TrackId(202), 8);
        var svc = db.ServiceFor(Me);
        var id = (await svc.CreateAsync(new CreateSessionRequest([101]))).Response!.SessionId;
        await svc.AppendOpsAsync(id, new AppendOpsRequest(1, [new CropToOp(0, 0, 4)]));   // cursor 2 → slot 0 = 5 pts
        await svc.AppendOpsAsync(id, new AppendOpsRequest(2, [new LoadActivityOp(202)])); // cursor 3, stamps [101,202]
        await svc.MoveCursorAsync(id, new MoveCursorRequest(2));                          // undo 202's load
        await db.MutateBlobAsync(new TrackId(202));                                                    // 202 stale — must be ignored

        var r = await db.SaveServiceFor(Me).SaveAsync(id, "replace", [new SlotCountClaim(0, 5)]);

        Assert.Null(r.ErrorKind);
        Assert.Equal(5, (await DecodeAsync(db, new TrackId(101))).Count);
        var commit = await new TrackEditCommitStore(db.Context).GetAsync(new CommitId(r.Response!.CommitId));
        Assert.Equal(new[] { new TrackId(101) }, commit!.ReplacedIds);
    }

    /// <summary>
    /// OPTION B, end to end — the exact shape of the incident this project exists for. Merging 202 into 101
    /// used to OVERWRITE 101 (its pre-merge geometry gone forever) and hide 202. Now the merge mints a NEW
    /// ride, and BOTH inputs go to the trash with their content untouched: delete the merged ride afterwards
    /// and the originals are still there, still restorable.
    /// </summary>
    [Fact]
    public async Task SaveReplace_Merge_CreatesANewRide_AndBothInputsSurviveInTheTrash()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me, name: "Base");
        await db.SeedRampAsync(new TrackId(101), 10);
        var baseBytes = await db.HeadBytesAsync(new TrackId(101));
        await db.AddActivityAsync(new TrackId(202), Me, name: "Leg 2");
        await db.SeedRampAsync(new TrackId(202), 6);
        var (id, claims) = await SessionWithAsync(db, [101, 202], new AppendOp(0, 1, TimeMode.Keep, null));

        var r = await db.SaveServiceFor(Me).SaveAsync(id, "replace", claims);

        Assert.Null(r.ErrorKind);

        // Both inputs are trashed — the base too, which is the whole change.
        foreach (int input in new[] { 101, 202 })
        {
            var a = await db.ActivityAsync(new TrackId(input));
            Assert.Equal(LifecycleStates.Trashed, a!.Lifecycle);
            Assert.Equal(LifecycleReasons.EditMerge, a.LifecycleReason);
        }

        // The base's own points are UNTOUCHED. This is what the old model destroyed.
        Assert.Equal(baseBytes, await db.HeadBytesAsync(new TrackId(101)));

        // The merged ride is a new activity, and it knows what it is made of.
        int mergedId = Assert.Single(r.Response!.Slots).TrackId;
        Assert.True(r.Response.Slots[0].Created);
        Assert.NotEqual(101, mergedId);
        Assert.Equal(LifecycleStates.Released, (await db.ActivityAsync(new TrackId(mergedId)))!.Lifecycle);
        Assert.Equal(16, (await DecodeAsync(db, new TrackId(mergedId))).Count);   // 10 + 6

        // Both inputs point at it, so the trash can say "merged into 'Base'".
        Assert.Equal(new TrackId(mergedId), (await db.ActivityAsync(new TrackId(101)))!.SupersededBy);
        Assert.Equal(new TrackId(mergedId), (await db.ActivityAsync(new TrackId(202)))!.SupersededBy);

        // Lineage: the merged version has two parents — the heads the inputs still carry.
        var mergedVersion = (await db.Content.GetHeadVersionIdAsync(new TrackId(mergedId)))!.Value;
        var parents = await db.Content.ParentsOfAsync(mergedVersion);
        Assert.Equal(2, parents.Count);
        Assert.Contains((await db.Content.GetHeadVersionIdAsync(new TrackId(101)))!.Value, parents);
        Assert.Contains((await db.Content.GetHeadVersionIdAsync(new TrackId(202)))!.Value, parents);

        var commit = await new TrackEditCommitStore(db.Context).GetAsync(new CommitId(r.Response.CommitId));
        Assert.Equal(new[] { new TrackId(202), new TrackId(101) }, commit!.TrashedIds.OrderByDescending(x => x));
        Assert.Equal(new[] { new TrackId(mergedId) }, commit.CreatedIds);

        // The consume left its audit twin — "why did my ride vanish?" must have an answer for the merge case
        // above all (the guarded raw-SQL transition pairs with the ActivityLifecycleSql log insert).
        foreach (int input in new[] { 101, 202 })
        {
            var log = Assert.Single(db.Context.ActivityLifecycleLogs.Where(l => l.TrackId == new TrackId(input)));
            Assert.Equal(LifecycleStates.Released, log.FromState);
            Assert.Equal(LifecycleStates.Trashed, log.ToState);
            Assert.Equal(LifecycleReasons.EditMerge, log.Reason);
            Assert.Equal(new CommitId(r.Response.CommitId), log.CommitId);
        }
    }

    [Fact]
    public async Task SaveReplace_Split_CreatesViaImportPath()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me, name: "Ride");
        await db.SeedRampAsync(new TrackId(101), 10);
        var (id, claims) = await SessionWithAsync(db, [101], new SplitAtOp(0, 5));

        var r = await db.SaveServiceFor(Me).SaveAsync(id, "replace", claims);

        Assert.Null(r.ErrorKind);
        var created = Assert.Single(r.Response!.Slots, s => s.Created);
        Assert.Equal("Ride (1)", created.Name);
        var act = await db.ActivityAsync(new TrackId(created.TrackId));
        Assert.Equal("track-edit", act!.Source);
        Assert.Equal(LifecycleStates.Released, act.Lifecycle);
        Assert.Equal("private", act.Visibility);                 // inherited from root
        Assert.Equal(new CommitId(r.Response.CommitId), act.EditedCommitId);
        Assert.Equal(0, act.ApiId);
        Assert.Equal(5, (await DecodeAsync(db, new TrackId(created.TrackId))).Count);   // tail = 5 pts
        Assert.Equal(6, (await DecodeAsync(db, new TrackId(101))).Count);              // head = 6 pts
    }

    [Fact]
    public async Task SaveCopy_CreatesEverything_TouchesNothing()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me, name: "Ride");
        await db.SeedRampAsync(new TrackId(101), 10);
        var original = (await db.HeadBytesAsync(new TrackId(101)))!;
        var (id, claims) = await SessionWithAsync(db, [101], new CropToOp(0, 0, 4));

        var r = await db.SaveServiceFor(Me).SaveAsync(id, "copy", claims);

        Assert.Null(r.ErrorKind);
        Assert.Equal(original, await db.HeadBytesAsync(new TrackId(101)));                 // byte-identical original
        Assert.Equal(LifecycleStates.Released, (await db.ActivityAsync(new TrackId(101)))!.Lifecycle);
        var commit = await new TrackEditCommitStore(db.Context).GetAsync(new CommitId(r.Response!.CommitId));
        Assert.Empty(commit!.ReplacedIds);
        Assert.Empty(commit.TrashedIds);
        // A copy versions only what it CREATED (that track's v1 carries the commit); the source is untouched.
        var commitVersions = await db.Content.VersionsOfCommitAsync(new CommitId(r.Response.CommitId));
        Assert.All(commitVersions, v => Assert.Equal(1, v.Seq));
        Assert.DoesNotContain(commitVersions, v => v.ActivityId == new TrackId(101));
        Assert.Single(await db.VersionsAsync(new TrackId(101)));                                 // source still at v1
        Assert.Equal("Ride (edited)", Assert.Single(r.Response.Slots).Name);
    }

    [Fact]
    public async Task Save_SecondCall_ReturnsSessionNotActive()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        var (id, claims) = await SessionWithAsync(db, [101], new CropToOp(0, 0, 4));
        var svc = db.SaveServiceFor(Me);
        await svc.SaveAsync(id, "replace", claims);

        var second = await svc.SaveAsync(id, "replace", claims);

        Assert.Equal(TrackEditApiErrors.SessionNotActive, second.Error!.Code);
        Assert.Single(await db.CommitIdsForAsync(Me));
    }

    [Fact]
    public async Task Save_LoadsOnly_ReturnsNothingToSave()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        var (id, claims) = await SessionWithAsync(db, [101]);

        var r = await db.SaveServiceFor(Me).SaveAsync(id, "replace", claims);

        Assert.Equal(TrackEditErrorKind.BadRequest, r.ErrorKind);
        Assert.Equal(TrackEditApiErrors.NothingToSave, r.Error!.Code);
    }

    [Fact]
    public async Task SaveReplace_SetsEditedCommitId()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        var (id, claims) = await SessionWithAsync(db, [101], new CropToOp(0, 0, 4));

        var r = await db.SaveServiceFor(Me).SaveAsync(id, "replace", claims);

        Assert.Equal(new CommitId(r.Response!.CommitId), (await db.ActivityAsync(new TrackId(101)))!.EditedCommitId);
    }

    [Fact]
    public async Task Save_EnqueuesDerive_TypeAndBuckets()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        await db.AddActivityAsync(new TrackId(202), Me);
        await db.SeedRampAsync(new TrackId(202), 6);
        var (id, claims) = await SessionWithAsync(db, [101, 202],
            new AppendOp(0, 1, TimeMode.Keep, null), new SplitAtOp(0, 5));

        var r = await db.SaveServiceFor(Me).SaveAsync(id, "replace", claims);

        // Option B: the merged slot and the split tail are BOTH new activities, and both inputs (101 included)
        // leave circulation — so nothing is re-derived in place, two rides need matching, and two need their
        // efforts removed.
        var createdIds = r.Response!.Slots.Where(s => s.Created).Select(s => s.TrackId).ToList();
        Assert.Equal(2, createdIds.Count);

        var item = await db.DequeueOneAsync();
        Assert.Equal(OperationQueue.OperationType.TrackEditDerive, item.Type);
        var payload = Assert.IsType<TrackEditDerivePayload>(item.Payload);
        Assert.Empty(payload.FullDeriveIds);                                  // nothing edited in place
        Assert.Equal(createdIds, payload.MatchOnlyIds);
        Assert.Equal(new[] { 202, 101 }, payload.RemoveOnlyIds);              // both inputs are in the trash
    }

    [Fact]
    public async Task Save_StagingAppearedMidSession_ReturnsStillRecording()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        var (id, claims) = await SessionWithAsync(db, [101], new CropToOp(0, 0, 4));
        await db.AddStagingAsync(new TrackId(101));

        var r = await db.SaveServiceFor(Me).SaveAsync(id, "replace", claims);

        Assert.Equal(TrackEditApiErrors.StillRecording, r.Error!.Code);
    }

    [Fact]
    public async Task Save_SourceBlobDeletedMidSession_ReturnsStaleTrack()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        var (id, claims) = await SessionWithAsync(db, [101], new CropToOp(0, 0, 4));
        await db.RemoveContentAsync(new TrackId(101));

        var r = await db.SaveServiceFor(Me).SaveAsync(id, "replace", claims);

        Assert.Equal(TrackEditApiErrors.StaleTrack, r.Error!.Code);
    }

    [Fact]
    public async Task Save_MarksSessionSaved()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        var (id, claims) = await SessionWithAsync(db, [101], new CropToOp(0, 0, 4));

        await db.SaveServiceFor(Me).SaveAsync(id, "replace", claims);

        Assert.Equal("saved", (await db.Sessions.GetAsync(id))!.Status);
    }

    [Fact]
    public async Task Preflight_ListsUpdatedTrashedCreatedNames_NoWrites()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me, name: "Ride");
        await db.SeedRampAsync(new TrackId(101), 10);
        await db.AddActivityAsync(new TrackId(202), Me, name: "Loop");
        await db.SeedRampAsync(new TrackId(202), 6);
        var (id, _) = await SessionWithAsync(db, [101, 202],
            new AppendOp(0, 1, TimeMode.Keep, null), new SplitAtOp(0, 5));

        var pf = await db.ServiceFor(Me).PreflightAsync(id, "replace");

        // The preflight is what the user reads before committing, so it must show the real consequence:
        // "10 rides move to trash", not a quiet in-place edit. Merged slot + split tail are both creations;
        // the base joins the ride it swallowed in the trash.
        Assert.Empty(pf!.Updated);
        Assert.Equal(new[] { 202, 101 }, pf.Trashed.Select(t => t.TrackId));
        Assert.Equal(new[] { "Ride", "Ride (1)" }, pf.Created.Select(c => c.Name), StringComparer.Ordinal);
        Assert.Empty(await db.CommitIdsForAsync(Me)); // dry-run: no writes
    }

    [Fact]
    public async Task SaveIdentityCrop_ProceedsAndSnapshots()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        var (id, claims) = await SessionWithAsync(db, [101], new CropToOp(0, 0, 9)); // whole-track crop (G27)

        var r = await db.SaveServiceFor(Me).SaveAsync(id, "replace", claims);

        Assert.Null(r.ErrorKind);
        Assert.Single(await db.Content.VersionsOfCommitAsync(new CommitId(r.Response!.CommitId)));
        Assert.Equal(10, (await DecodeAsync(db, new TrackId(101))).Count);
    }

    // ---- §14.4 synchronous stats/bbox/start (T-V17/T-V18) ----

    [Fact]
    public async Task SaveReplace_StatsBboxStartLoc_SynchronousAndAtomic()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);          // lat 47.000..47.009
        await db.SeedBboxAsync(new TrackId(101), 47.000, 47.009, 11.0, 11.0); // full-track bbox (imports write one)
        var before = await db.BboxAsync(new TrackId(101));
        var (id, claims) = await SessionWithAsync(db, [101], new CropToOp(0, 2, 6)); // keep lat 47.002..47.006

        var r = await db.SaveServiceFor(Me).SaveAsync(id, "replace", claims);

        Assert.Null(r.ErrorKind);
        var act = await db.ActivityAsync(new TrackId(101));
        Assert.True(act!.TotalDistance > 0);                    // recomputed from the cropped blob, in-request
        Assert.Equal(new DateTime(2025, 1, 1, 8, 0, 2, DateTimeKind.Utc), act.StartDateTimeUtc);
        var after = await db.BboxAsync(new TrackId(101));
        Assert.True(after!.Value.MaxLat < before!.Value.MaxLat);   // bbox SHRANK (expand-only misfit pin)
        Assert.True(after.Value.MinLat > before.Value.MinLat);
    }

    [Fact]
    public async Task SaveReplace_SensorStats_RecomputedNulledWhenAbsent()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        var t0 = new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        await db.SeedBlobAsync(new TrackId(101), Enumerable.Range(0, 10).Select(i => new GpsData
        {
            GpsLat = 47.0 + i * 0.001, GpsLong = 11.0, DateTimeUtc = t0.AddSeconds(i), HeartRate = 100 + i,
        }).ToList()); // heartRate present, power absent
        var (id, claims) = await SessionWithAsync(db, [101], new CropToOp(0, 0, 4));

        await db.SaveServiceFor(Me).SaveAsync(id, "replace", claims);

        var act = await db.ActivityAsync(new TrackId(101));
        Assert.NotNull(act!.HeartRateAvg);
        Assert.Null(act.PowerAvg);       // whole-NULL power channel → NULL column
    }
}
