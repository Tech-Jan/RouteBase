using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.TrackEdit;
using RouteBaseWeb.Services.Tracks;
using RouteBaseWeb.Tests.Services.TrackEdit;   // TrackEditTestDb — the harness lives with the editor's tests
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Tracks;

/// <summary>
/// The version history — what the append-only revision list is FOR. Every state a ride was ever in is still on
/// record, so restoring one is a head move over an object that already exists (zero new bytes), and the
/// provenance of each version is DERIVED from the lineage DAG rather than stored as a label that could drift.
/// </summary>
public class TrackVersionHistoryTests
{
    private const string Me = "me";

    private static TrackVersionHistory HistoryFor(TrackEditTestDb db) => db.HistoryFor(Me);

    private static async Task<SaveResponse> SaveAsync(
        TrackEditTestDb db, IReadOnlyList<int> loadIds, params EditOp[] ops)
    {
        var svc = db.ServiceFor(Me);
        var state = (await svc.CreateAsync(new CreateSessionRequest(loadIds))).Response!;
        state = (await svc.AppendOpsAsync(state.SessionId, new AppendOpsRequest(state.Cursor, ops))).Response!;
        var claims = state.SlotCounts.Select(c => new SlotCountClaim(c.SlotId, c.PointCount)).ToList();
        var r = await db.SaveServiceFor(Me).SaveAsync(state.SessionId, "replace", claims);
        Assert.Null(r.ErrorKind);
        return r.Response!;
    }

    [Fact]
    public async Task History_ListsEveryState_NewestFirst_WithTheHeadFlagged()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        await SaveAsync(db, [101], new CropToOp(0, 0, 4));

        var history = await HistoryFor(db).ListAsync(new TrackId(101));

        Assert.Equal(new[] { 2, 1 }, history.Select(v => v.Seq));      // newest first
        Assert.True(history[0].IsHead);
        Assert.False(history[1].IsHead);
        Assert.Equal(5, history[0].PointCount);                        // the crop
        Assert.Equal(10, history[1].PointCount);                       // …and what it was before
        Assert.Equal("Edited", history[0].Provenance);
        Assert.Equal("Imported", history[1].Provenance);
    }

    /// <summary>
    /// Restoring an old version costs ZERO new bytes — the object is already stored under its own hash — and
    /// history stays append-only: nothing is rewritten, and "restored v1" is itself a point in the record.
    /// </summary>
    [Fact]
    public async Task RestoreAVersion_AppendsToHistory_CostsNoNewBytes_AndRebuildsTheStats()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        var original = (await db.HeadBytesAsync(new TrackId(101)))!;
        await SaveAsync(db, [101], new CropToOp(0, 0, 4));
        int objectsBefore = await db.ObjectCountAsync();
        double? croppedDistance = (await db.ActivityAsync(new TrackId(101)))!.TotalDistance;
        var v1 = (await HistoryFor(db).ListAsync(new TrackId(101))).Single(v => v.Seq == 1);

        Assert.Null(await HistoryFor(db).RestoreAsync(new TrackId(101), new VersionId(v1.VersionId)));

        Assert.Equal(original, await db.HeadBytesAsync(new TrackId(101)));          // the old points are back…
        Assert.Equal(objectsBefore, await db.ObjectCountAsync());      // …and stored zero times over

        var history = await HistoryFor(db).ListAsync(new TrackId(101));
        Assert.Equal(new[] { 3, 2, 1 }, history.Select(v => v.Seq));   // appended, not rewritten
        Assert.True(history[0].IsHead);
        Assert.Equal("Restored an earlier version", history[0].Provenance);
        Assert.Equal(10, history[0].PointCount);

        // The denormalized scalars follow the head, or the ride would show the cropped distance forever.
        double? restoredDistance = (await db.ActivityAsync(new TrackId(101)))!.TotalDistance;
        Assert.NotNull(restoredDistance);
        Assert.True(restoredDistance > croppedDistance,
            $"restored distance {restoredDistance} should exceed the cropped {croppedDistance}");
    }

    /// <summary>The restored geometry is what segments/efforts/photos must now reflect — restore re-derives
    /// exactly like the editor's save and revert do, or they'd keep describing the pre-restore points.</summary>
    [Fact]
    public async Task RestoreAVersion_EnqueuesTheDeriveChain()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        await SaveAsync(db, [101], new CropToOp(0, 0, 4));
        await db.DequeueOneAsync();                                    // the save's own derive item
        var v1 = (await HistoryFor(db).ListAsync(new TrackId(101))).Single(v => v.Seq == 1);

        Assert.Null(await HistoryFor(db).RestoreAsync(new TrackId(101), new VersionId(v1.VersionId)));

        var payload = Assert.IsType<TrackEditDerivePayload>((await db.DequeueOneAsync()).Payload);
        Assert.Equal(new[] { 101 }, payload.FullDeriveIds);
        Assert.Null(payload.CommitId);                                 // a restore is not an edit script
        Assert.Empty(payload.MatchOnlyIds);
        Assert.Empty(payload.RemoveOnlyIds);
    }

    /// <summary>A live session's staged tail rides on the current head — the same guard save/revert have.</summary>
    [Fact]
    public async Task RestoreAVersion_IsRefusedWhileTheTrackIsRecording()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        await SaveAsync(db, [101], new CropToOp(0, 0, 4));
        var v1 = (await HistoryFor(db).ListAsync(new TrackId(101))).Single(v => v.Seq == 1);
        await db.AddStagingAsync(new TrackId(101));

        Assert.Equal(TrackEditApiErrors.StillRecording, await HistoryFor(db).RestoreAsync(new TrackId(101), new VersionId(v1.VersionId)));

        Assert.Equal(2, (await HistoryFor(db).ListAsync(new TrackId(101))).Count);  // nothing appended
    }

    /// <summary>A recompute (elevation refresh, re-finalize) carries one parent and no commit — same DAG shape
    /// as a restore. What tells them apart is the object: a restore reuses its parent's sha, a recompute writes
    /// a new one. The label must not claim the user restored something they didn't.</summary>
    [Fact]
    public async Task ASystemRecompute_IsNotLabelledAsARestore()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        await db.SeedRampAsync(new TrackId(101), 7);                                // new bytes, no commit — a recompute

        var history = await HistoryFor(db).ListAsync(new TrackId(101));

        Assert.Equal("Recalculated (elevation refresh)", history[0].Provenance);
        Assert.Equal("Imported", history[1].Provenance);
    }

    [Fact]
    public async Task ARecordedRide_SaysRecorded_NotImported()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me, source: "live-recording");
        await db.SeedRampAsync(new TrackId(101), 10);

        var v1 = Assert.Single(await HistoryFor(db).ListAsync(new TrackId(101)));

        Assert.Equal("Recorded", v1.Provenance);
    }

    [Fact]
    public async Task AMergedRide_SaysWhatItIsMadeOf()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me, name: "Base");
        await db.SeedRampAsync(new TrackId(101), 10);
        await db.AddActivityAsync(new TrackId(202), Me, name: "Leg 2");
        await db.SeedRampAsync(new TrackId(202), 6);
        var save = await SaveAsync(db, [101, 202], new AppendOp(0, 1, TimeMode.Keep, null));
        int mergedId = Assert.Single(save.Slots).TrackId;

        var v1 = Assert.Single(await HistoryFor(db).ListAsync(new TrackId(mergedId)));

        // Provenance is read off the lineage DAG, not off a stored label.
        Assert.Equal("Merged from 2 rides", v1.Provenance);
        Assert.Equal(["Base", "Leg 2"], v1.ParentNames.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    /// <summary>Purge a merge's inputs and the merged ride still knows it was merged — it just can no longer
    /// say from what. The dangling parent link IS that fact (parent_version_id carries no FK for this reason).</summary>
    [Fact]
    public async Task AMergedRide_WhoseInputsWerePurged_SaysSo()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me, name: "Base");
        await db.SeedRampAsync(new TrackId(101), 10);
        await db.AddActivityAsync(new TrackId(202), Me, name: "Leg 2");
        await db.SeedRampAsync(new TrackId(202), 6);
        var save = await SaveAsync(db, [101, 202], new AppendOp(0, 1, TimeMode.Keep, null));
        int mergedId = Assert.Single(save.Slots).TrackId;

        await db.PurgeActivityAsync(new TrackId(101));
        await db.PurgeActivityAsync(new TrackId(202));

        var v1 = Assert.Single(await HistoryFor(db).ListAsync(new TrackId(mergedId)));

        Assert.Equal("Merged — the rides it came from have been deleted", v1.Provenance);
        Assert.Empty(v1.ParentNames);
    }

    // ---- the batch-read pins (CL-01): the same views must come out of a constant-count read ----

    /// <summary>A long edit chain is the ordinary case, and every one of its parents is a version the reader
    /// ALREADY holds. Pins the whole rendered list — order, head flag, provenance, parent names, same-as-parent —
    /// so resolving same-track parents from memory instead of re-querying them cannot change a single field.</summary>
    [Fact]
    public async Task History_WithManyVersions_ResolvesSameTrackParentsWithoutRefetchingThem()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 30);
        foreach (int last in new[] { 24, 20, 16, 12, 8 })
            await SaveAsync(db, [101], new CropToOp(0, 0, last));

        var history = await HistoryFor(db).ListAsync(new TrackId(101));

        Assert.Equal(new[] { 6, 5, 4, 3, 2, 1 }, history.Select(v => v.Seq));
        Assert.Equal(new[] { 9, 13, 17, 21, 25, 30 }, history.Select(v => v.PointCount));
        Assert.Equal(
            new[] { "Edited", "Edited", "Edited", "Edited", "Edited", "Imported" },
            history.Select(v => v.Provenance), StringComparer.Ordinal);
        Assert.Equal(new[] { true, false, false, false, false, false }, history.Select(v => v.IsHead));
        Assert.All(history, v => Assert.Empty(v.ParentNames));      // same-track parents are not "another ride"
        Assert.All(history, v => Assert.False(v.SameAsParent));     // every crop wrote new content
    }

    /// <summary>A merge's parents live on OTHER tracks — the one case the reader cannot answer from the version
    /// list it already loaded. Pins that the cross-track fetch still names the source ride.</summary>
    [Fact]
    public async Task History_MergeWithACrossTrackParent_StillNamesTheSourceRide()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me, name: "Base");
        await db.SeedRampAsync(new TrackId(101), 10);
        await db.AddActivityAsync(new TrackId(202), Me, name: "Leg 2");
        await db.SeedRampAsync(new TrackId(202), 6);
        var save = await SaveAsync(db, [101, 202], new AppendOp(0, 1, TimeMode.Keep, null));
        int mergedId = Assert.Single(save.Slots).TrackId;

        var v1 = Assert.Single(await HistoryFor(db).ListAsync(new TrackId(mergedId)));

        Assert.Equal("Merged from 2 rides", v1.Provenance);
        Assert.Equal(["Base", "Leg 2"], v1.ParentNames.Order(StringComparer.Ordinal), StringComparer.Ordinal);
        Assert.False(v1.SameAsParent);
        Assert.True(v1.IsHead);
    }

    /// <summary>Purge ONE of a merge's two inputs and the surviving one is still named, while the count stays 2 —
    /// the DAG remembers what it was made of. Pins the mixed present/missing parent case, the one the batched
    /// resolution has to get right per version rather than globally.</summary>
    [Fact]
    public async Task History_WithAPurgedParent_StillReportsItMissing()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me, name: "Base");
        await db.SeedRampAsync(new TrackId(101), 10);
        await db.AddActivityAsync(new TrackId(202), Me, name: "Leg 2");
        await db.SeedRampAsync(new TrackId(202), 6);
        var save = await SaveAsync(db, [101, 202], new AppendOp(0, 1, TimeMode.Keep, null));
        int mergedId = Assert.Single(save.Slots).TrackId;

        await db.PurgeActivityAsync(new TrackId(101));   // one input gone, the other still there

        var v1 = Assert.Single(await HistoryFor(db).ListAsync(new TrackId(mergedId)));

        Assert.Equal("Merged from 2 rides", v1.Provenance);   // not all parents missing → still a plain merge
        Assert.Equal(["Leg 2"], v1.ParentNames);              // only the survivor can be named
    }

    // ---- reset to original (the one destructive call on an append-only record) ----

    /// <summary>The whole point: the ride goes back to the points it started with, and the history it collected
    /// on the way is gone. The surviving version must BE the original — not a new row that merely copies it.</summary>
    [Fact]
    public async Task ResetToOriginal_PutsTheRideBackToItsFirstPoints_AndEmptiesTheHistory()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        var originalId = (await HistoryFor(db).ListAsync(new TrackId(101))).Single().VersionId;
        await SaveAsync(db, [101], new CropToOp(0, 0, 4));
        await SaveAsync(db, [101], new CropToOp(0, 0, 2));   // edit twice — a history worth resetting

        Assert.Null(await HistoryFor(db).ResetToOriginalAsync(new TrackId(101)));

        var version = Assert.Single(await HistoryFor(db).ListAsync(new TrackId(101)));
        Assert.True(version.IsHead);
        Assert.Equal(originalId, version.VersionId);   // the original row itself is the head again
        Assert.Equal(10, version.PointCount);          // all 10 points back — both crops undone
    }

    /// <summary>The denormalized scalars are read straight off Activity all over the app, so a reset that moved
    /// the points but left the distance describing the cropped track would be a silent lie.</summary>
    [Fact]
    public async Task ResetToOriginal_RebuildsTheStatsFromTheRestoredPoints()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        await SaveAsync(db, [101], new CropToOp(0, 0, 4));
        double? croppedDistance = (await db.ActivityAsync(new TrackId(101)))!.TotalDistance;
        Assert.NotNull(croppedDistance);

        await HistoryFor(db).ResetToOriginalAsync(new TrackId(101));

        // The full ramp is longer than the 5-point crop of it. If the scalars did not follow the head back, the
        // ride would keep showing the cropped distance over the restored points — a silent lie.
        double? resetDistance = (await db.ActivityAsync(new TrackId(101)))!.TotalDistance;
        Assert.NotNull(resetDistance);
        Assert.True(resetDistance > croppedDistance,
            $"reset distance {resetDistance} should exceed the cropped {croppedDistance}");
    }

    /// <summary>The geometry moved, so segment matches, efforts, heatmaps and photo positions derived from the
    /// edited track are stale — the same derive chain a restore enqueues.</summary>
    [Fact]
    public async Task ResetToOriginal_EnqueuesTheDeriveChain()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        await SaveAsync(db, [101], new CropToOp(0, 0, 4));
        await db.DequeueOneAsync();   // the save's own derive

        await HistoryFor(db).ResetToOriginalAsync(new TrackId(101));

        var payload = Assert.IsType<TrackEditDerivePayload>((await db.DequeueOneAsync()).Payload);
        Assert.Equal(new[] { 101 }, payload.FullDeriveIds);
        Assert.Null(payload.CommitId);   // a reset is not an edit script
    }

    /// <summary>The original's parent edge would point into the chain we just deleted. Left dangling, the reader
    /// calls a never-merged ride "merged from rides that have been deleted" — so it goes with them, and the ride
    /// reads as exactly what it is again: the thing that was imported.</summary>
    [Fact]
    public async Task ResetToOriginal_LeavesTheRideLookingLikeItWasNeverEdited()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        await SaveAsync(db, [101], new CropToOp(0, 0, 4));

        await HistoryFor(db).ResetToOriginalAsync(new TrackId(101));

        var version = Assert.Single(await HistoryFor(db).ListAsync(new TrackId(101)));
        Assert.Equal("Imported", version.Provenance);
        Assert.Empty(version.ParentNames);
        Assert.Null((await db.ActivityAsync(new TrackId(101)))!.EditedCommitId);   // nothing left to "undo this save" to
    }

    /// <summary>Content is shared by hash, so the sweep is a reference check, not a delete-what-I-deleted: the
    /// edited object is now referenced by nothing and goes; the original's is the head's and stays.</summary>
    [Fact]
    public async Task ResetToOriginal_SweepsTheObjectsNothingReferencesAnyMore()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        await SaveAsync(db, [101], new CropToOp(0, 0, 4));
        Assert.Equal(2, await db.ObjectCountAsync());   // the original + the cropped

        await HistoryFor(db).ResetToOriginalAsync(new TrackId(101));

        Assert.Equal(1, await db.ObjectCountAsync());   // only what the head still points at
    }

    /// <summary>Same guard as restore/save: a live recording's staged tail rides on the current head.</summary>
    [Fact]
    public async Task ResetToOriginal_IsRefusedWhileTheTrackIsRecording()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        await SaveAsync(db, [101], new CropToOp(0, 0, 4));
        await db.AddStagingAsync(new TrackId(101));

        Assert.Equal(TrackEditApiErrors.StillRecording, await HistoryFor(db).ResetToOriginalAsync(new TrackId(101)));

        Assert.Equal(2, (await HistoryFor(db).ListAsync(new TrackId(101))).Count);   // nothing deleted
        Assert.Equal(5, (await HistoryFor(db).ListAsync(new TrackId(101)))[0].PointCount);   // …and the crop still stands
    }

    /// <summary>A ride with nothing to reset is not an error — it is already in the state asked for.</summary>
    [Fact]
    public async Task ResetToOriginal_OnARideThatWasNeverEdited_IsANoOp()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);

        Assert.Null(await HistoryFor(db).ResetToOriginalAsync(new TrackId(101)));

        Assert.Single(await HistoryFor(db).ListAsync(new TrackId(101)));
        Assert.Equal(10, (await HistoryFor(db).ListAsync(new TrackId(101))).Single().PointCount);
    }

    // ==== mutation-testing additions ====

    // L66 (`a.TrackId == trackId` → `!=`): ownership is an EXACT match. With a single owned activity the `!=`
    // mutant inverts BOTH answers — the owned track reads unowned, and any other id reads owned.
    [Fact]
    public async Task OwnsAsync_IsExactMatch_OnTheOwnedTrack()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);   // the ONLY owned activity

        Assert.True(await HistoryFor(db).OwnsAsync(new TrackId(101)));
        Assert.False(await HistoryFor(db).OwnsAsync(new TrackId(999)));
    }

    // L152 (`return "notFound"` → `""`): a reset on a track with no version rows is a notFound, with that code.
    [Fact]
    public async Task ResetToOriginal_TrackWithNoVersions_ReturnsNotFound()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);   // Activity row, no content/versions

        Assert.Equal("notFound", await HistoryFor(db).ResetToOriginalAsync(new TrackId(101)));
    }

    // L153 (`return "notFound"` → `""`): versions exist but none is flagged head (a corrupt/headless track) →
    // notFound, with that code. Forcing is_head=0 is the only way to reach this branch past the count guard.
    [Fact]
    public async Task ResetToOriginal_VersionsButNoHead_ReturnsNotFound()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        await db.Context.Database.ExecuteSqlRawAsync(
            "UPDATE Activity_Version SET is_head = 0 WHERE track_id = {0};", 101);

        Assert.Equal("notFound", await HistoryFor(db).ResetToOriginalAsync(new TrackId(101)));
    }

    // L158 Logical (`&&` → `||`): the idempotent early return fires ONLY when the doomed set is empty AND the
    // head is the original. If the head is already the original ROW but later versions still exist, the reset
    // must still delete them — the `||` mutant returns early and leaves them behind.
    [Fact]
    public async Task ResetToOriginal_HeadAlreadyAtOriginalRow_StillDeletesLaterVersions()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);   // seq 1 = original
        await db.SeedRampAsync(new TrackId(101), 7);    // seq 2 = a different head
        var seq1 = (await db.VersionsAsync(new TrackId(101))).OrderBy(v => v.Seq).First().VersionId;
        // Point the head back at the original ROW itself (not a restore copy) while seq 2 still exists.
        await db.Context.Database.ExecuteSqlRawAsync(
            "UPDATE Activity_Version SET is_head = 0 WHERE track_id = {0};", 101);
        await db.Context.Database.ExecuteSqlRawAsync(
            "UPDATE Activity_Version SET is_head = 1 WHERE version_id = {0};", seq1.Value);

        Assert.Null(await HistoryFor(db).ResetToOriginalAsync(new TrackId(101)));

        Assert.Single(await HistoryFor(db).ListAsync(new TrackId(101)));   // seq 2 deleted; the || mutant keeps it
    }

    // L205 Logical (`||` → `&&`) + String: restoring a version id that does not exist is a notFound, with that
    // code. The `&&` mutant dereferences the null target (NRE) instead of returning notFound.
    [Fact]
    public async Task RestoreAsync_NonExistentVersion_ReturnsNotFound()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);

        Assert.Equal("notFound", await HistoryFor(db).RestoreAsync(new TrackId(101), new VersionId(999999)));
    }

    // L205 Logical: a version that exists but belongs to ANOTHER track is also notFound — the `&&` mutant lets
    // it through (target non-null) and tries to restore a foreign version.
    [Fact]
    public async Task RestoreAsync_VersionFromAnotherTrack_ReturnsNotFound()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        await db.AddActivityAsync(new TrackId(202), Me);
        await db.SeedRampAsync(new TrackId(202), 6);
        var foreign = (await db.VersionsAsync(new TrackId(202))).Single().VersionId;

        Assert.Equal("notFound", await HistoryFor(db).RestoreAsync(new TrackId(101), foreign));
    }

    // L276 (`return "Split from another ride"` → `""`) + L292 (`a.TrackName ?? $"Activity {id}"` → `""`):
    // a version with exactly ONE cross-track parent is a split, and its source is named — falling back to
    // "Activity {id}" when the source ride has no name of its own.
    [Fact]
    public async Task History_SplitFromAnotherRide_NamesTheUnnamedSource()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddSuggestibleAsync(new TrackId(101), Me, name: null);   // source ride, no name
        await db.SeedRampAsync(new TrackId(101), 10);
        await db.AddActivityAsync(new TrackId(202), Me);
        await db.SeedRampAsync(new TrackId(202), 6);
        var source = (await db.VersionsAsync(new TrackId(101))).Single().VersionId;
        var split = (await db.VersionsAsync(new TrackId(202))).Single().VersionId;
        await db.Content.AddParentsAsync(split, new[] { source });        // one cross-track parent = a split

        var v = Assert.Single(await HistoryFor(db).ListAsync(new TrackId(202)));

        Assert.Equal("Split from another ride", v.Provenance);
        Assert.Equal(new[] { "Activity 101" }, v.ParentNames);
    }

    // (L117 — the `$"Activity {id}"` fallback in ListAsync — is UNREACHABLE, hence not tested: a foreign
    // parent only reaches parentTracks when its VERSION resolves, and Activity_Version.track_id FK-cascades
    // with Activity, so a resolved version's Activity row is always present and NamesByIdAsync always names it.
    // Verified empirically: deleting the Activity row cascade-deletes the version → the id goes MISSING, never
    // "resolved-but-unnamed". An equivalent mutant.)

    // L190(`_invalidator.InvalidateUser(userId);` → removed): a reset moves the head, so the owner's cached
    // slices/coords are stale and their change token must fire.
    [Fact]
    public async Task ResetToOriginal_InvalidatesTheOwnersCache()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        await SaveAsync(db, [101], new CropToOp(0, 0, 4));
        var (history, token) = HistoryWithCacheToken(db, Me);

        Assert.Null(await history.ResetToOriginalAsync(new TrackId(101)));

        Assert.True(token.HasChanged);
    }

    // L222 (`_invalidator.InvalidateUser(userId);` → removed): a restore moves the head — same staleness.
    [Fact]
    public async Task RestoreAVersion_InvalidatesTheOwnersCache()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 10);
        await SaveAsync(db, [101], new CropToOp(0, 0, 4));
        var v1 = (await HistoryFor(db).ListAsync(new TrackId(101))).Single(v => v.Seq == 1);
        var (history, token) = HistoryWithCacheToken(db, Me);

        Assert.Null(await history.RestoreAsync(new TrackId(101), new VersionId(v1.VersionId)));

        Assert.True(token.HasChanged);
    }

    // A TrackVersionHistory wired to an invalidator we can observe (TrackEditTestDb.HistoryFor builds its own,
    // unreachable one). The returned token flips HasChanged the moment InvalidateUser(user) runs.
    private static (TrackVersionHistory history, IChangeToken token) HistoryWithCacheToken(TrackEditTestDb db, string user)
    {
        var invalidator = new UserCacheInvalidator();
        var token = invalidator.GetChangeToken(user);
        var history = new TrackVersionHistory(
            db.Context, db.Content, new TestVisibilityService(user),
            new GpsNormalizationService(), new PassThroughResolver(), db.Staging,
            new RouteBaseWeb.Services.TrackEdit.TrackDeriveQueue(
                db.Queue, NullLogger<RouteBaseWeb.Services.TrackEdit.TrackDeriveQueue>.Instance),
            db.Objects, invalidator, NullLogger<TrackVersionHistory>.Instance);
        return (history, token);
    }

    private sealed class PassThroughResolver : IActivityTimezoneResolver
    {
        public Task<TimeSpan?> ResolveOffsetAsync(double? lat, double? lng, DateTime startUtc, string userId)
            => Task.FromResult<TimeSpan?>(null);
    }
}
