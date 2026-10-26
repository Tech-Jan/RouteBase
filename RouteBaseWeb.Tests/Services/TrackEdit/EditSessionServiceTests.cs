using RouteBaseWeb.Models;
using RouteBaseWeb.Services.TrackEdit;
using RouteBaseWeb.Services.Tracks;
using Xunit;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Tests.Services.TrackEdit;

/// <summary>
/// Session lifecycle + guards + validation replay + write-through + sources (A2 §3/§14.1). Real stores +
/// engine + codec over an in-memory SQLite DB (<see cref="TrackEditTestDb"/>); the guard's owner/review/
/// staging/blob checks and the stamp/replay authority are all exercised end to end.
/// </summary>
public class EditSessionServiceTests
{
    private const string Me = "me";
    private const string Other = "other";

    // ---- create ----

    [Fact]
    public async Task Create_SeedsLoadOps_StampsAndCursor()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 5);

        var r = await db.ServiceFor(Me).CreateAsync(new CreateSessionRequest([101]));

        Assert.Null(r.ErrorKind);
        var s = r.Response!;
        Assert.Equal("active", s.Status);
        Assert.Equal(1, s.Cursor);
        var op = Assert.IsType<LoadActivityOp>(Assert.Single(s.Script.Ops));
        Assert.Equal(101, op.ActivityId);
        var stamp = Assert.Single(s.Stamps);
        Assert.Equal(101, stamp.ActivityId);
        Assert.Equal(5, stamp.PointCount);
        Assert.NotEmpty(stamp.BlobSha256);
        Assert.Equal(new[] { (0, 5) }, s.SlotCounts.Select(c => (c.SlotId, c.PointCount)));
    }

    [Fact]
    public async Task Create_NonOwnedActivity_Fails()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me, Other);
        await db.AddActivityAsync(new TrackId(102), Other);
        await db.SeedRampAsync(new TrackId(102), 5);

        var r = await db.ServiceFor(Me).CreateAsync(new CreateSessionRequest([102]));

        Assert.Equal(TrackEditErrorKind.NotFound, r.ErrorKind);
        Assert.Empty(await db.SessionIdsForAsync(Me));
    }

    [Fact]
    public async Task Create_PendingActivity_ReturnsActivityNotEditable()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(103), Me, lifecycle: LifecycleStates.InWork);
        await db.SeedRampAsync(new TrackId(103), 5);

        var r = await db.ServiceFor(Me).CreateAsync(new CreateSessionRequest([103]));

        Assert.Equal(TrackEditErrorKind.Conflict, r.ErrorKind);
        Assert.Equal(TrackEditApiErrors.ActivityNotEditable, r.Error!.Code);
        Assert.Equal(103, r.Error.ActivityId);
    }

    [Fact]
    public async Task Create_NoBlob_ReturnsNoTrackData()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(104), Me);

        var r = await db.ServiceFor(Me).CreateAsync(new CreateSessionRequest([104]));

        Assert.Equal(TrackEditErrorKind.Conflict, r.ErrorKind);
        Assert.Equal(TrackEditApiErrors.NoTrackData, r.Error!.Code);
    }

    [Fact]
    public async Task Create_WithStagingRows_ReturnsStillRecording()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(105), Me);
        await db.SeedRampAsync(new TrackId(105), 5);
        await db.AddStagingAsync(new TrackId(105));

        var r = await db.ServiceFor(Me).CreateAsync(new CreateSessionRequest([105]));

        Assert.Equal(TrackEditErrorKind.Conflict, r.ErrorKind);
        Assert.Equal(TrackEditApiErrors.StillRecording, r.Error!.Code);
    }

    [Fact]
    public async Task Create_DuplicateIds_ReturnsDuplicateLoad()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 5);

        var r = await db.ServiceFor(Me).CreateAsync(new CreateSessionRequest([101, 101]));

        Assert.Equal(TrackEditErrorKind.BadRequest, r.ErrorKind);
        Assert.Equal(TrackEditErrors.DuplicateLoad, r.Error!.Code);
    }

    [Fact]
    public async Task Create_OverMaxSlots_ReturnsSlotCapExceeded()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        var ids = Enumerable.Range(1, TrackEditLimits.MaxSlots + 1).ToArray();

        var r = await db.ServiceFor(Me).CreateAsync(new CreateSessionRequest(ids));

        Assert.Equal(TrackEditErrorKind.BadRequest, r.ErrorKind);
        Assert.Equal(TrackEditErrors.SlotCapExceeded, r.Error!.Code);
    }

    // ---- check-out: the lock ----

    [Fact]
    public async Task Create_ChecksOutTheRide()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 5);

        var r = await db.ServiceFor(Me).CreateAsync(new CreateSessionRequest([101]));

        Assert.Equal(r.Response!.SessionId, (await db.ActivityAsync(new TrackId(101)))!.InEditSessionId);
    }

    /// <summary>One open edit per ride, enforced by the schema — a second checkout finds the pointer set and is
    /// refused with 409 rather than opening a session that would race the first one at save.</summary>
    [Fact]
    public async Task Create_RideAlreadyCheckedOut_Returns409_AndWritesNoSession()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 5);
        var svc = db.ServiceFor(Me);
        var first = await svc.CreateAsync(new CreateSessionRequest([101]));

        var second = await svc.CreateAsync(new CreateSessionRequest([101]));

        Assert.Equal(TrackEditErrorKind.Conflict, second.ErrorKind);
        Assert.Equal(TrackEditApiErrors.AlreadyCheckedOut, second.Error!.Code);
        // The rolled-back session must leave no row, and the ride stays checked out to the FIRST session.
        Assert.Equal([first.Response!.SessionId], await db.SessionIdsForAsync(Me));
        Assert.Equal(first.Response.SessionId, (await db.ActivityAsync(new TrackId(101)))!.InEditSessionId);
    }

    /// <summary>The pointer is a column on the ride, so a permanent purge takes it with the row — the one
    /// track-scoped edit fact a purge could not reach before.</summary>
    [Fact]
    public async Task Purge_OfCheckedOutRide_TakesThePointerWithIt()
    {
        var (db, id) = await ActiveSessionAsync();
        await using var _ = db;

        await db.PurgeActivityAsync(new TrackId(101));

        Assert.Null(await db.ActivityAsync(new TrackId(101)));
        Assert.NotNull(await db.Sessions.GetAsync(id)); // the session row survives (it is user-keyed, not track-keyed)
    }

    // ---- append ----

    private static async Task<(TrackEditTestDb Db, string SessionId)> ActiveSessionAsync(int points = 10)
    {
        var db = await TrackEditTestDb.CreateAsync("me");
        await db.AddActivityAsync(new TrackId(101), "me");
        await db.SeedRampAsync(new TrackId(101), points);
        var created = await db.ServiceFor("me").CreateAsync(new CreateSessionRequest([101]));
        return (db, created.Response!.SessionId);
    }

    [Fact]
    public async Task Append_TruncatesRedoTail_AndPersists()
    {
        var (db, id) = await ActiveSessionAsync();
        await using var _ = db;
        var svc = db.ServiceFor(Me);

        await svc.AppendOpsAsync(id, new AppendOpsRequest(1, [new CropToOp(0, 0, 6)]));      // cursor 2
        await svc.AppendOpsAsync(id, new AppendOpsRequest(2, [new DeleteRangeOp(0, 1, 2, TimeMode.Keep, null)])); // cursor 3
        await svc.MoveCursorAsync(id, new MoveCursorRequest(1));                             // back to 1

        var r = await svc.AppendOpsAsync(id, new AppendOpsRequest(1, [new CropToOp(0, 0, 3)]));

        Assert.Null(r.ErrorKind);
        Assert.Equal(2, r.Response!.Cursor);
        Assert.Equal(2, r.Response.Script.Ops.Count);          // load + the new crop; redo tail dropped
        Assert.IsType<CropToOp>(r.Response.Script.Ops[1]);
        Assert.Equal(4, Assert.Single(r.Response.SlotCounts).PointCount); // crop 0..3 → 4 pts
    }

    [Fact]
    public async Task Append_NewLoadActivity_RecordsStamp()
    {
        var (db, id) = await ActiveSessionAsync();
        await using var _ = db;
        await db.AddActivityAsync(new TrackId(202), Me);
        await db.SeedRampAsync(new TrackId(202), 6);

        var r = await db.ServiceFor(Me).AppendOpsAsync(id, new AppendOpsRequest(1, [new LoadActivityOp(202)]));

        Assert.Null(r.ErrorKind);
        Assert.Equal(new[] { 101, 202 }, r.Response!.Stamps.Select(s => s.ActivityId));
    }

    [Fact]
    public async Task AppendOps_TruncatingLoad_PrunesItsStamp()
    {
        var (db, id) = await ActiveSessionAsync();
        await using var _ = db;
        await db.AddActivityAsync(new TrackId(202), Me);
        await db.SeedRampAsync(new TrackId(202), 6);
        var svc = db.ServiceFor(Me);
        await svc.AppendOpsAsync(id, new AppendOpsRequest(1, [new LoadActivityOp(202)])); // cursor 2, stamps [101,202]
        await svc.MoveCursorAsync(id, new MoveCursorRequest(1));                          // undo 202's load

        // Appending off cursor 1 truncates 202's load away → its stamp must be pruned.
        var r = await svc.AppendOpsAsync(id, new AppendOpsRequest(1, [new CropToOp(0, 0, 4)]));

        Assert.Null(r.ErrorKind);
        Assert.Equal(new[] { 101 }, r.Response!.Stamps.Select(s => s.ActivityId));
    }

    [Fact]
    public async Task Append_InvalidOpGeometry_Rejected_SessionUnchanged()
    {
        var (db, id) = await ActiveSessionAsync();
        await using var _ = db;

        var r = await db.ServiceFor(Me).AppendOpsAsync(id, new AppendOpsRequest(1, [new CropToOp(0, 0, 999)]));

        Assert.Equal(TrackEditErrorKind.Unprocessable, r.ErrorKind);
        Assert.Equal(TrackEditErrors.RangeOutOfBounds, r.Error!.Code);
        Assert.Equal(1, r.Error.OpIndex);
        var row = await db.Sessions.GetAsync(id);
        Assert.Equal(1, row!.Cursor);
        Assert.Single(row.Script.Ops);
    }

    [Fact]
    public async Task Append_BaseCursorMismatch_ReturnsCursorConflict()
    {
        var (db, id) = await ActiveSessionAsync();
        await using var _ = db;

        var r = await db.ServiceFor(Me).AppendOpsAsync(id, new AppendOpsRequest(0, [new CropToOp(0, 0, 5)]));

        Assert.Equal(TrackEditErrorKind.Conflict, r.ErrorKind);
        Assert.Equal(TrackEditApiErrors.CursorConflict, r.Error!.Code);
    }

    [Fact]
    public async Task Append_OverMaxOps_ReturnsScriptTooLong()
    {
        var (db, id) = await ActiveSessionAsync();
        await using var _ = db;
        var many = Enumerable.Range(0, TrackEditLimits.MaxOps).Select(_ => (EditOp)new CropToOp(0, 0, 5)).ToList();

        var r = await db.ServiceFor(Me).AppendOpsAsync(id, new AppendOpsRequest(1, many));

        Assert.Equal(TrackEditErrorKind.BadRequest, r.ErrorKind);
        Assert.Equal(TrackEditErrors.ScriptTooLong, r.Error!.Code);
    }

    // ---- cursor ----

    [Fact]
    public async Task CursorMove_PersistsWithinBounds_RejectsOutside()
    {
        var (db, id) = await ActiveSessionAsync();
        await using var _ = db;
        var svc = db.ServiceFor(Me);
        await svc.AppendOpsAsync(id, new AppendOpsRequest(1, [new CropToOp(0, 0, 6)])); // 2 ops

        Assert.Null((await svc.MoveCursorAsync(id, new MoveCursorRequest(0))).ErrorKind);
        Assert.Null((await svc.MoveCursorAsync(id, new MoveCursorRequest(2))).ErrorKind);

        var bad = await svc.MoveCursorAsync(id, new MoveCursorRequest(3));
        Assert.Equal(TrackEditErrorKind.BadRequest, bad.ErrorKind);
        Assert.Equal(TrackEditApiErrors.CursorOutOfRange, bad.Error!.Code);
    }

    // ---- get / list / discard ----

    [Fact]
    public async Task Get_OtherUsersSession_NotFound()
    {
        var (db, id) = await ActiveSessionAsync();
        await using var _ = db;

        var r = await db.ServiceFor(Other).GetAsync(id);

        Assert.Equal(TrackEditErrorKind.NotFound, r.ErrorKind);
    }

    [Fact]
    public async Task Discard_DeletesSession_ReleasesTheRide_FurtherOpsRejected()
    {
        var (db, id) = await ActiveSessionAsync();
        await using var _ = db;
        var svc = db.ServiceFor(Me);

        Assert.Null((await svc.DiscardAsync(id)).ErrorKind);
        Assert.Null(await db.Sessions.GetAsync(id));                       // the row is GONE, not flagged
        Assert.Null((await db.ActivityAsync(new TrackId(101)))!.InEditSessionId);       // …and the FK released the ride

        var append = await svc.AppendOpsAsync(id, new AppendOpsRequest(1, [new CropToOp(0, 0, 5)]));
        Assert.Equal(TrackEditErrorKind.NotFound, append.ErrorKind);
    }

    /// <summary>Abort is the escape hatch that makes the lock safe: after it, the ride is editable again.</summary>
    [Fact]
    public async Task Discard_ThenCheckoutAgain_Succeeds()
    {
        var (db, id) = await ActiveSessionAsync();
        await using var _ = db;
        var svc = db.ServiceFor(Me);
        await svc.DiscardAsync(id);

        var again = await svc.CreateAsync(new CreateSessionRequest([101]));

        Assert.Null(again.ErrorKind);
        Assert.Equal(again.Response!.SessionId, (await db.ActivityAsync(new TrackId(101)))!.InEditSessionId);
    }

    [Fact]
    public async Task Discard_Idempotent_SecondCallOk()
    {
        var (db, id) = await ActiveSessionAsync();
        await using var _ = db;
        var svc = db.ServiceFor(Me);

        Assert.Null((await svc.DiscardAsync(id)).ErrorKind);
        Assert.Null((await svc.DiscardAsync(id)).ErrorKind); // second call is a no-op 200
    }

    [Fact]
    public async Task Discard_AfterConcurrentSaveFlip_Returns409()
    {
        var (db, id) = await ActiveSessionAsync();
        await using var _ = db;
        // Another tab's save flipped the session active → saved (blob already overwritten).
        Assert.True(await db.Sessions.TrySetStatusAsync(id, "active", "saved", DateTime.UtcNow));

        var r = await db.ServiceFor(Me).DiscardAsync(id);

        Assert.Equal(TrackEditErrorKind.Conflict, r.ErrorKind);
        Assert.Equal(TrackEditApiErrors.SessionNotActive, r.Error!.Code);
    }

    // ---- resume hint: the Details page's whole resume lookup (F-1) ----

    [Fact]
    public async Task ResumeHint_CarriesSessionIdAndEditCount_AndReadsNoTrackBlobs()
    {
        var (db, id) = await ActiveSessionAsync();
        await using var _ = db;
        await db.ServiceFor(Me).AppendOpsAsync(id, new AppendOpsRequest(1, [new SplitAtOp(0, 3)]));

        var hint = await db.ServiceFor(Me, new TrackEditTestDb.ThrowingReader()).GetResumeHintAsync(101);

        Assert.NotNull(hint);
        Assert.Equal(id, hint!.SessionId);
        Assert.Equal(1, hint.EditCount); // the seed loadActivity is not an edit
    }

    [Fact]
    public async Task ResumeHint_NullForRideWithoutSession_ForForeignOwner_AndAfterDiscard()
    {
        var (db, id) = await ActiveSessionAsync();
        await using var _ = db;
        await db.AddActivityAsync(new TrackId(202), Me);

        Assert.Null(await db.ServiceFor(Me).GetResumeHintAsync(202));    // no session on that ride
        Assert.Null(await db.ServiceFor(Other).GetResumeHintAsync(101)); // never leak another user's session

        await db.ServiceFor(Me).DiscardAsync(id);
        Assert.Null(await db.ServiceFor(Me).GetResumeHintAsync(101));    // only an ACTIVE session resumes
    }

    // ---- the badge: /Activities shows a checked-out ride as being edited ----

    [Fact]
    public async Task GridRow_IsInEdit_TrueWhileCheckedOut_FalseAfterDiscard()
    {
        var (db, id) = await ActiveSessionAsync();
        await using var _ = db;
        await db.AddActivityAsync(new TrackId(202), Me);   // never checked out

        Assert.True(ActivityRowDto.From((await db.ActivityAsync(new TrackId(101)))!, Me, null).IsInEdit);
        Assert.False(ActivityRowDto.From((await db.ActivityAsync(new TrackId(202)))!, Me, null).IsInEdit);

        await db.ServiceFor(Me).DiscardAsync(id);
        Assert.False(ActivityRowDto.From((await db.ActivityAsync(new TrackId(101)))!, Me, null).IsInEdit);
    }

    // ---- read paths read no blobs (TE-01) ----

    [Fact]
    public async Task Get_ReadsNoTrackBlobs_AndStillCountsSlots()
    {
        var (db, id) = await ActiveSessionAsync();
        await using var _ = db;
        await db.ServiceFor(Me).AppendOpsAsync(id, new AppendOpsRequest(1, [new SplitAtOp(0, 3)]));

        // A reader that throws on any decode: GetAsync must still produce the split's two slot counts.
        var r = await db.ServiceFor(Me, new TrackEditTestDb.ThrowingReader()).GetAsync(id);

        Assert.Null(r.ErrorKind);
        Assert.Equal(
            new[] { (0, 4), (1, 7) }, // head = index+1, tail = count-index (10 pts, split at 3)
            r.Response!.SlotCounts.Select(c => (c.SlotId, c.PointCount)));
    }

    [Fact]
    public async Task MoveCursor_ReadsNoTrackBlobs_AndStillCountsSlots()
    {
        var (db, id) = await ActiveSessionAsync();
        await using var _ = db;
        await db.ServiceFor(Me).AppendOpsAsync(id, new AppendOpsRequest(1, [new CropToOp(0, 0, 6)])); // 7 pts

        var undone = await db.ServiceFor(Me, new TrackEditTestDb.ThrowingReader())
            .MoveCursorAsync(id, new MoveCursorRequest(1)); // undo the crop → back to the 10-pt source

        Assert.Null(undone.ErrorKind);
        Assert.Equal(10, Assert.Single(undone.Response!.SlotCounts).PointCount);
    }

    // ---- store addition pin ----

    [Fact]
    public async Task Store_TrySetStatus_ConditionalOnFromStatus()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        var now = DateTime.UtcNow;
        var snap = new EditSessionSnapshot(
            "s-cond", Me, new EditScript(1, [new LoadActivityOp(1)]), 1, [], "active", now, now);
        await db.Sessions.CreateAsync(snap);

        Assert.True(await db.Sessions.TrySetStatusAsync("s-cond", "active", "saved", now));
        Assert.False(await db.Sessions.TrySetStatusAsync("s-cond", "active", "saved", now));
    }

    // ---- sources (§14.1) ----

    [Fact]
    public async Task Sources_ReturnsAbsoluteQuantizedChannels_FixtureSemantics()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        var t0 = new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        var points = new[]
        {
            new GpsData { GpsLat = 47.123456, GpsLong = 8.54, DateTimeUtc = t0, HeartRate = 120 },
            new GpsData { GpsLat = 47.123556, GpsLong = 8.54, DateTimeUtc = t0.AddSeconds(1), HeartRate = null },
            new GpsData { GpsLat = 47.123656, GpsLong = 8.54, DateTimeUtc = t0.AddSeconds(2), HeartRate = 122 },
        };
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedBlobAsync(new TrackId(101), points);
        var id = (await db.ServiceFor(Me).CreateAsync(new CreateSessionRequest([101]))).Response!.SessionId;

        var r = await db.ServiceFor(Me).GetSourcesAsync(id);

        Assert.Null(r.ErrorKind);
        var src = Assert.Single(r.Response!.Sources);
        Assert.Equal(3, src.Count);
        long epoch0 = (long)(t0 - DateTime.UnixEpoch).TotalSeconds;
        Assert.Equal(new long?[] { epoch0, epoch0 + 1, epoch0 + 2 }, src.Channels["time"]);
        Assert.Equal(47123456L, src.Channels["lat"]![0]);
        Assert.Equal(new long?[] { 120, null, 122 }, src.Channels["heartRate"]); // NullQ → null
        Assert.False(src.Channels.ContainsKey("altitude"));                       // whole-NULL → key absent
    }

    [Fact]
    public async Task Sources_StaleBlob_Returns409StaleTrack()
    {
        var (db, id) = await ActiveSessionAsync();
        await using var _ = db;
        await db.MutateBlobAsync(new TrackId(101));

        var r = await db.ServiceFor(Me).GetSourcesAsync(id);

        Assert.Equal(TrackEditErrorKind.Conflict, r.ErrorKind);
        Assert.Equal(TrackEditApiErrors.StaleTrack, r.Error!.Code);
        Assert.Equal(101, r.Error.ActivityId);
    }

    [Fact]
    public async Task Sources_CarriesTzAndCount_InLoadOrder()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me, tzHours: 2f);
        await db.SeedRampAsync(new TrackId(101), 5);
        await db.AddActivityAsync(new TrackId(202), Me, tzHours: -3f);
        await db.SeedRampAsync(new TrackId(202), 8);
        var svc = db.ServiceFor(Me);
        var create = await svc.CreateAsync(new CreateSessionRequest([101]));
        var id = create.Response!.SessionId;
        await svc.AppendOpsAsync(id, new AppendOpsRequest(1, [new LoadActivityOp(202)]));

        var r = await svc.GetSourcesAsync(id);

        Assert.Null(r.ErrorKind);
        Assert.Equal(new[] { 101, 202 }, r.Response!.Sources.Select(s => s.ActivityId));
        Assert.Equal(2.0, r.Response.Sources[0].TzOffsetHours);
        Assert.Equal(5, r.Response.Sources[0].Count);
        Assert.Equal(-3.0, r.Response.Sources[1].TzOffsetHours);
        Assert.Equal(8, r.Response.Sources[1].Count);
    }

    [Fact]
    public async Task Sources_IdsFilter_ReturnsSubset_RejectsUnstampedIds()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me);
        await db.SeedRampAsync(new TrackId(101), 5);
        await db.AddActivityAsync(new TrackId(202), Me);
        await db.SeedRampAsync(new TrackId(202), 8);
        var svc = db.ServiceFor(Me);
        var id = (await svc.CreateAsync(new CreateSessionRequest([101]))).Response!.SessionId;
        await svc.AppendOpsAsync(id, new AppendOpsRequest(1, [new LoadActivityOp(202)]));

        var subset = await svc.GetSourcesAsync(id, "202");
        Assert.Null(subset.ErrorKind);
        Assert.Equal(202, Assert.Single(subset.Response!.Sources).ActivityId);

        var invalid = await svc.GetSourcesAsync(id, "999");
        Assert.Equal(TrackEditErrorKind.BadRequest, invalid.ErrorKind);
        Assert.Equal(TrackEditApiErrors.InvalidSourceIds, invalid.Error!.Code);
    }

    [Fact]
    public async Task Sources_CarriesActivityName_WithFallback()
    {
        await using var db = await TrackEditTestDb.CreateAsync(Me);
        await db.AddActivityAsync(new TrackId(101), Me, name: "Ride A");
        await db.SeedRampAsync(new TrackId(101), 5);
        await db.AddActivityAsync(new TrackId(202), Me);
        await db.SeedRampAsync(new TrackId(202), 5);
        var noName = await db.Context.Activities.FindAsync(new TrackId(202));
        noName!.TrackName = null;
        await db.Context.SaveChangesAsync();
        var svc = db.ServiceFor(Me);
        var id = (await svc.CreateAsync(new CreateSessionRequest([101]))).Response!.SessionId;
        await svc.AppendOpsAsync(id, new AppendOpsRequest(1, [new LoadActivityOp(202)]));

        var r = await svc.GetSourcesAsync(id);

        Assert.Null(r.ErrorKind);
        Assert.Equal("Ride A", r.Response!.Sources[0].Name);
        Assert.Equal("Activity 202", r.Response.Sources[1].Name); // null name → fallback
    }
}
