using RouteBaseWeb.Api;
using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Heatmaps;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Tests.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Lifecycle;

/// <summary>
/// The trash, end to end against a real (in-memory) SQLite context: delete hides a ride without destroying it,
/// restore brings it back to where it came from, and a purge that would take an un-doable merge with it is
/// refused until the caller says yes. This is the flow the original incident had no answer for.
/// </summary>
public class TrashFlowTests : IAsyncLifetime
{
    private const string Me = "me";

    private RouteBaseDbContext _context = null!;
    private SqliteConnection _conn = null!;
    private TestVisibilityService _visibility = null!;
    private ActivityLifecycleService _lifecycle = null!;
    private TrashQueryService _trash = null!;
    private ActivityCommandService _command = null!;

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        await _conn.OpenAsync();
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(_conn).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();
        // The purge path deletes from every table in ChildTableRegistry, and the raw-DDL ones among them are
        // not EF entities, so EnsureCreated does not build them.
        var schema = new DbSchemaManager(_context);
        await schema.EnsureTrackContentTablesAsync();
        await schema.EnsureRecordingStagingTableAsync();
        await schema.EnsureActivityBboxTableAsync();

        _context.Users.Add(new AppUser
        {
            UserId = Me, DisplayName = Me, Email = "me@test.com", IsAdmin = false,
            CreatedAt = DateTime.UtcNow.ToString("O"), LastLogin = DateTime.UtcNow.ToString("O"),
        });
        await _context.SaveChangesAsync();

        var invalidator = new UserCacheInvalidator();
        _visibility = new TestVisibilityService(Me);
        // The REAL heatmap lifecycle: marking a ride's heatmaps stale is a genuine side-effect of leaving (or
        // entering) circulation, and the tests below assert on it. The tile store is never touched by it.
        var heatmaps = new HeatmapLifecycle(
            _context, _visibility,
            new HeatmapTilePackStore(
                new HeatmapStorageOptions { Root = Path.Combine(Path.GetTempPath(), $"trash_{Guid.NewGuid():N}") },
                new HeatmapTileIndexCache(new MemoryCache(new MemoryCacheOptions())), TimeProvider.System),
            NullLogger<HeatmapLifecycle>.Instance);
        _lifecycle  = TestLifecycle.For(_context, _visibility, invalidator, heatmaps);
        _trash      = new TrashQueryService(_context, _visibility);
        _command    = new ActivityCommandService(
            _context, _visibility, invalidator, NullLogger<ActivityCommandService>.Instance, _lifecycle);
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        await _conn.DisposeAsync();
    }

    private async Task<TrackId> AddRideAsync(string name, TrackId trackId)
    {
        _context.Activities.Add(new Activity
        {
            TrackId = trackId, UserId = Me, TrackName = name, ActivityType = "Ride",
            Lifecycle = LifecycleStates.Released, TotalDistance = 10_000,
        });
        await _context.SaveChangesAsync();
        return new TrackId(trackId.Value);
    }

    private async Task<Activity> ReloadAsync(TrackId trackId)
    {
        _context.ChangeTracker.Clear();
        return await _context.Activities.FirstAsync(a => a.TrackId == trackId);
    }

    [Fact]
    public async Task Delete_TrashesTheRide_WithoutDestroyingIt()
    {
        var id = await AddRideAsync("Morning Ride", new TrackId(1));

        Assert.True(await _command.DeleteActivityAsync(new TrackId(id.Value)));

        var a = await ReloadAsync(new TrackId(id.Value));
        Assert.Equal(LifecycleStates.Trashed, a.Lifecycle);
        Assert.Equal(LifecycleReasons.User, a.LifecycleReason);
        Assert.Equal(LifecycleStates.Released, a.LifecyclePrev);   // restore knows where to put it back

        // Gone from circulation, still very much on disk.
        Assert.Empty(await _visibility.VisibleActivities(_context.Activities).ToListAsync());
        Assert.Single(await _trash.ListAsync());
    }

    [Fact]
    public async Task Restore_ReturnsTheRideToWhereItCameFrom()
    {
        var id = await AddRideAsync("Evening Ride", new TrackId(2));
        await _command.DeleteActivityAsync(new TrackId(id.Value));

        Assert.True((await _lifecycle.RestoreAsync(new TrackId(id.Value))).Ok);

        var a = await ReloadAsync(new TrackId(id.Value));
        Assert.Equal(LifecycleStates.Released, a.Lifecycle);
        Assert.Empty(await _trash.ListAsync());
        Assert.Single(await _visibility.VisibleActivities(_context.Activities).ToListAsync());
    }

    [Fact]
    public async Task RejectedAutoRide_RestoresToTheReviewQueue_NotToYourActivities()
    {
        _context.Activities.Add(new Activity
        {
            TrackId = new TrackId(3), UserId = Me, TrackName = "Auto", ActivityType = "Ride",
            Source = "auto-recording",
            Lifecycle = LifecycleStates.InWork, LifecycleReason = LifecycleReasons.AwaitingReview,
        });
        await _context.SaveChangesAsync();

        await _lifecycle.TransitionAsync(new TrackId(3), LifecycleStates.Trashed, LifecycleReasons.ReviewRejected);
        Assert.True((await _lifecycle.RestoreAsync(new TrackId(3))).Ok);

        // Restore is a transition, not a lookup (invariant 3): it goes back to in-work, not into circulation.
        var a = await ReloadAsync(new TrackId(3));
        Assert.Equal(LifecycleStates.InWork, a.Lifecycle);
        Assert.Equal(LifecycleReasons.AwaitingReview, a.LifecycleReason);
    }

    [Fact]
    public async Task ParkedAutoRide_RestoresAsUser_NotAwaitingReview()
    {
        // An auto-recorded ride the user deliberately parked (not the review queue's rejection) and
        // later deleted must restore as a plain parked ride, not re-enter the review queue — the
        // restore reason must come from why THIS trash happened (LifecycleReason), not from Source
        // (arch finding B: lifecycle must not infer review-queue membership from a recording-owned
        // column).
        _context.Activities.Add(new Activity
        {
            TrackId = new TrackId(7), UserId = Me, TrackName = "Auto", ActivityType = "Ride",
            Source = "auto-recording", Lifecycle = LifecycleStates.Released,
        });
        await _context.SaveChangesAsync();

        await _lifecycle.TransitionAsync(new TrackId(7), LifecycleStates.InWork, LifecycleReasons.User);  // parked
        await _lifecycle.TransitionAsync(new TrackId(7), LifecycleStates.Trashed, LifecycleReasons.User); // deleted
        Assert.True((await _lifecycle.RestoreAsync(new TrackId(7))).Ok);

        var a = await ReloadAsync(new TrackId(7));
        Assert.Equal(LifecycleStates.InWork, a.Lifecycle);
        Assert.Equal(LifecycleReasons.User, a.LifecycleReason);
    }

    [Fact]
    public async Task EveryTransition_LeavesAnAuditRow()
    {
        var id = await AddRideAsync("Logged", new TrackId(4));
        await _command.DeleteActivityAsync(new TrackId(id.Value));
        await _lifecycle.RestoreAsync(new TrackId(id.Value));

        var log = await _context.ActivityLifecycleLogs
            .Where(l => l.TrackId == new TrackId(id.Value)).OrderBy(l => l.LogId).ToListAsync();

        Assert.Equal(2, log.Count);
        Assert.Equal((LifecycleStates.Released, LifecycleStates.Trashed), (log[0].FromState, log[0].ToState));
        Assert.Equal((LifecycleStates.Trashed, LifecycleStates.Released), (log[1].FromState, log[1].ToState));
    }

    // ---- invariant 4: losing an un-merge must be an informed choice ----

    [Fact]
    public async Task MergedAwaySource_IsFlaggedInTheTrash_AsLongAsTheMergeProductLives()
    {
        var product = await AddRideAsync("Big Ride", new TrackId(10));
        var source  = await AddRideAsync("Leg 1", new TrackId(11));

        await _lifecycle.TransitionAsync(source, LifecycleStates.Trashed, LifecycleReasons.EditMerge);
        var a = await ReloadAsync(new TrackId(source.Value));
        a.SupersededBy = new TrackId(product.Value);
        await _context.SaveChangesAsync();

        var row = Assert.Single(await _trash.ListAsync());
        Assert.Equal(LifecycleReasons.EditMerge, row.Reason);
        Assert.Equal("Big Ride", row.MergedIntoName);   // the UI's "merged into 'X'"

        // Purging it would destroy the un-merge → the trash flags it for confirmation.
        Assert.Equal([new TrackId(source.Value)], await _trash.MergeSourcesAmongAsync([new TrackId(source.Value)]));
    }

    [Fact]
    public async Task PurgingTheMergeProduct_UnSupersedesItsSources_SoTheyArePlainTrashAgain()
    {
        var product = await AddRideAsync("Big Ride", new TrackId(20));
        var source  = await AddRideAsync("Leg 1", new TrackId(21));

        await _lifecycle.TransitionAsync(source, LifecycleStates.Trashed, LifecycleReasons.EditMerge);
        var a = await ReloadAsync(new TrackId(source.Value));
        a.SupersededBy = new TrackId(product.Value);
        await _context.SaveChangesAsync();

        // The FK is ON DELETE SET NULL: nothing supersedes the source any more, because the thing that did
        // is gone. There is no merge left to protect, so purging the source now needs no confirmation.
        Assert.True(await _command.DeleteActivityCoreAsync(new TrackId(product.Value), Me));

        var row = Assert.Single(await _trash.ListAsync());
        Assert.Null(row.MergedIntoId);
        Assert.Empty(await _trash.MergeSourcesAmongAsync([new TrackId(source.Value)]));
    }

    // CC-02: /Activities renders the ride's LOCAL day (ActivityRowDto.StartLocal). The trash grid must render
    // the same day — it used to project StartDateTimeUtc, which for a 00:30 local (UTC+2) ride is the PREVIOUS
    // calendar day, so the two grids disagreed about when the same ride happened.
    [Fact]
    public async Task ListAsync_StartedAt_IsTheLocalStart_NotTheUtcOne()
    {
        _context.Activities.Add(new Activity
        {
            TrackId = new TrackId(60), UserId = Me, TrackName = "Midnight Ride", ActivityType = "Ride",
            Lifecycle = LifecycleStates.Released,
            StartDateTimeUtc   = new DateTime(2026, 7, 11, 22, 30, 0, DateTimeKind.Unspecified),
            StartDateTimeLocal = new DateTime(2026, 7, 12, 0, 30, 0, DateTimeKind.Unspecified),
        });
        await _context.SaveChangesAsync();
        await _lifecycle.TransitionAsync(new TrackId(60), LifecycleStates.Trashed, LifecycleReasons.User);

        var row = Assert.Single(await _trash.ListAsync());

        Assert.Equal("2026-07-12T00:30:00", row.StartedIso);  // FE-12 wall-clock string (AR-02/E2 suffix)
        Assert.StartsWith("2026-07-12", row.StartedIso, StringComparison.Ordinal);      // the same calendar day /Activities shows
    }

    // TrashedAt is a true instant, not a wall clock. SQLite reads it back Kind=Unspecified; the DTO
    // field is a DateTimeOffset (AR-02), so the guard is that projection stamps a ZERO offset — not
    // the server-local offset the implicit cast would have assumed.
    [Fact]
    public async Task ListAsync_TrashedAt_CarriesUtcKind()
    {
        var id = await AddRideAsync("Evening Ride", new TrackId(61));
        await _lifecycle.TransitionAsync(new TrackId(id.Value), LifecycleStates.Trashed, LifecycleReasons.User);

        var row = Assert.Single(await _trash.ListAsync());

        Assert.NotNull(row.TrashedAt);
        Assert.Equal(TimeSpan.Zero, row.TrashedAt!.Value.Offset);
    }

    // IsTrashedAsync is the gate on the trash's own GPS read (GET /api/trash/{id}/gps-summary), which the card
    // minimaps need — the activities endpoint of that name is visible-gated and so reads a trashed ride empty.
    // Ownership alone must not open it: only the caller's OWN trash.
    [Fact]
    public async Task IsTrashed_IsTrueOnlyForYourOwnTrash()
    {
        var mine = await AddRideAsync("Morning Ride", new TrackId(30));
        var alsoMine = await AddRideAsync("Still Riding", new TrackId(31));
        await _command.DeleteActivityAsync(new TrackId(mine.Value));

        Assert.True(await _trash.IsTrashedAsync(new TrackId(mine.Value)));
        Assert.False(await _trash.IsTrashedAsync(new TrackId(alsoMine.Value)));   // released: not trash, no trash-scoped read
        Assert.False(await _trash.IsTrashedAsync(new TrackId(999)));        // does not exist
    }

    // ---- leaving/entering circulation marks the ride's heatmaps stale (CL-04) ----

    /// <summary>Seed a shapes segment carrying a <paramref name="status"/> heatmap; the ride has efforts on it
    /// only when <paramref name="trackId"/> is given.</summary>
    private async Task<SegmentId> AddHeatmappedSegmentAsync(string status, TrackId? trackId = null)
    {
        var seg = new Segment { UserId = Me, SegmentKind = "shapes", SegmentDistance = 100 };
        _context.Segments.Add(seg);
        await _context.SaveChangesAsync();
        _context.Heatmaps.Add(new Heatmap { SegmentId = seg.SegmentId, Status = status });
        if (trackId is { } t)
            _context.GpsInSegments.Add(new GpsInSegment { TrackId = new TrackId(t.Value), SegmentId = seg.SegmentId });
        await _context.SaveChangesAsync();
        return seg.SegmentId;
    }

    private async Task<string> StatusAsync(SegmentId segmentId)
    {
        _context.ChangeTracker.Clear();
        return (await _context.Heatmaps.AsNoTracking().FirstAsync(h => h.SegmentId == segmentId)).Status!;
    }

    /// <summary>Effort rows survive a trash — only VisibleActivities filters them — but heatmap tiles are
    /// PAINTED, so a trashed ride's heat would sit in them until something else triggered a regeneration.</summary>
    [Fact]
    public async Task Trashing_MarksEveryHeatmapTheRideTouchesStale()
    {
        var id = await AddRideAsync("Heated", new TrackId(60));
        var touchedA = await AddHeatmappedSegmentAsync("done", new TrackId(id.Value));
        var touchedB = await AddHeatmappedSegmentAsync("done", new TrackId(id.Value));
        var untouched = await AddHeatmappedSegmentAsync("done");

        Assert.True(await _command.DeleteActivityAsync(new TrackId(id.Value)));

        Assert.Equal("stale", await StatusAsync(touchedA));
        Assert.Equal("stale", await StatusAsync(touchedB));
        Assert.Equal("done", await StatusAsync(untouched));   // a ride it has no efforts on is not its business
    }

    [Fact]
    public async Task Restoring_MarksThemStaleAgain()
    {
        var id = await AddRideAsync("Heated", new TrackId(61));
        var touched = await AddHeatmappedSegmentAsync("done", new TrackId(id.Value));
        var pending = await AddHeatmappedSegmentAsync("pending", new TrackId(id.Value));
        await _command.DeleteActivityAsync(new TrackId(id.Value));

        // Back into circulation: the tiles painted without it are wrong again.
        _context.Heatmaps.First(h => h.SegmentId == touched).Status = "done";
        await _context.SaveChangesAsync();

        Assert.True((await _lifecycle.RestoreAsync(new TrackId(id.Value))).Ok);

        Assert.Equal("stale", await StatusAsync(touched));
        Assert.Equal("pending", await StatusAsync(pending));   // only 'done' heatmaps go stale
    }

    // ---- the purge gate (CL-02): it takes a SET OF IDS, not the rendering projection ----

    /// <summary>The controller under test, wired to the same services as the rest of the fixture. The GPS
    /// service is not on the purge path (it serves the minimap read), so it is never touched here.
    ///
    /// The tracker is cleared first: a purge request gets a FRESH scoped context in production, whereas this
    /// fixture's context still holds the entities the arrange step wrote. Leaving them tracked would make EF
    /// client-cascade the lifecycle-log rows that the purge's raw SQL has already deleted.</summary>
    private TrashApiController Controller()
    {
        _context.ChangeTracker.Clear();
        return new(_trash, _lifecycle, _command, gps: null!, _visibility, NullLogger<TrashApiController>.Instance);
    }

    private static int Purged(IActionResult result)
    {
        var ok = Assert.IsType<OkObjectResult>(result);
        return Assert.IsType<PurgeResponse>(ok.Value).Purged;
    }

    /// <summary>Two independent gates on a purge naming someone else's row: the trash-scope filter (only ids
    /// in the caller's own trash reach the core) and the core's own owner-id assertion. Both must hold: a
    /// released ride of mine and a trashed ride of someone else's must both survive a purge that names them.</summary>
    [Fact]
    public async Task Purge_OnlyTouchesRowsInTheCallersOwnTrash()
    {
        var mineReleased = await AddRideAsync("Mine, still here", new TrackId(41));
        _context.Users.Add(new AppUser
        {
            UserId = "other", DisplayName = "other", Email = "other@test.com", IsAdmin = false,
            CreatedAt = DateTime.UtcNow.ToString("O"), LastLogin = DateTime.UtcNow.ToString("O"),
        });
        _context.Activities.Add(new Activity
        {
            TrackId = new TrackId(42), UserId = "other", TrackName = "Theirs", ActivityType = "Ride",
            Lifecycle = LifecycleStates.Trashed, LifecyclePrev = LifecycleStates.Released,
        });
        await _context.SaveChangesAsync();

        var result = await Controller().Purge(new PurgeRequest([mineReleased.Value, 42]), CancellationToken.None);

        Assert.Equal(0, Purged(result));
        Assert.Equal(2, await _context.Activities.CountAsync());   // both rows still there
    }

    [Fact]
    public async Task EmptyTrash_PurgesEveryTrashedRow_AndNothingElse()
    {
        foreach (int id in new[] { 50, 51, 52 })
            await _command.DeleteActivityAsync(await AddRideAsync($"Binned {id}", new TrackId(id)));
        await AddRideAsync("Kept 53", new TrackId(53));
        await AddRideAsync("Kept 54", new TrackId(54));

        var result = await Controller().Empty(new PurgeRequest(null), CancellationToken.None);

        Assert.Equal(3, Purged(result));
        _context.ChangeTracker.Clear();
        Assert.Equal([new TrackId(53), new TrackId(54)], await _context.Activities.Select(a => a.TrackId).OrderBy(id => id).ToListAsync());
    }

    /// <summary>Seed a merge: <paramref name="source"/> trashed via EditMerge and superseded by
    /// <paramref name="product"/>, which stays released — the exact shape invariant 4 protects.</summary>
    private async Task SeedLiveMergeAsync(int product, int source)
    {
        await AddRideAsync("Big Ride", new TrackId(product));
        await AddRideAsync("Leg 1", new TrackId(source));
        await _lifecycle.TransitionAsync(new TrackId(source), LifecycleStates.Trashed, LifecycleReasons.EditMerge);
        var a = await ReloadAsync(new TrackId(source));
        a.SupersededBy = new TrackId(product);
        await _context.SaveChangesAsync();
    }

    private static T GetValue<T>(IActionResult result, string prop) where T : notnull
    {
        // Body is now the named MergeSourcesConfirmBody (PascalCase props); tests ask by the wire
        // (camelCase) name, so match case-insensitively.
        var value = Assert.IsType<ConflictObjectResult>(result).Value!;
        return (T)value.GetType().GetProperty(prop,
            System.Reflection.BindingFlags.IgnoreCase | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)!
            .GetValue(value)!;
    }

    /// <summary>The safety gate this class exists for: purging a merge SOURCE while its merge PRODUCT still
    /// lives must be refused, not silently allowed, so "empty trash" can never quietly destroy an un-merge.</summary>
    [Fact]
    public async Task Purge_OfALiveMergeSource_IsRefused_AndTheRowSurvives()
    {
        await SeedLiveMergeAsync(product: 70, source: 71);

        var result = await Controller().Purge(new PurgeRequest([71]), CancellationToken.None);

        Assert.Equal("mergeSourcesNeedConfirm", GetValue<string>(result, "code")); // D32: token moved error→code
        Assert.Equal([71], GetValue<List<int>>(result, "mergeSourceIds"));
        Assert.Contains("can never be undone", GetValue<string>(result, "message"), StringComparison.Ordinal);

        var row = await ReloadAsync(new TrackId(71));
        Assert.Equal(LifecycleStates.Trashed, row.Lifecycle);   // still in the trash, not destroyed
        Assert.Single(await _trash.ListAsync());
    }

    /// <summary>Confirming is the ONLY way past the gate: same request, same ids, but
    /// <c>ConfirmMergeSources=true</c> — now it actually purges.</summary>
    [Fact]
    public async Task Purge_OfALiveMergeSource_WithConfirmation_ActuallyPurges()
    {
        await SeedLiveMergeAsync(product: 72, source: 73);

        var result = await Controller().Purge(
            new PurgeRequest([73], ConfirmMergeSources: true), CancellationToken.None);

        Assert.Equal(1, Purged(result));
        Assert.Empty(await _context.Activities.Where(a => a.TrackId == new TrackId(73)).ToListAsync());
        Assert.Empty(await _trash.ListAsync());
    }

    /// <summary>Purging the source ALONGSIDE its still-live product in the same request does not exempt it:
    /// the gate does not special-case "you're taking both, so nothing dangles" — it always demands an
    /// explicit, informed confirmation. Pins the current (deliberately conservative) behavior.</summary>
    [Fact]
    public async Task Purge_OfSourceAndProductTogether_StillRequiresConfirmation()
    {
        await SeedLiveMergeAsync(product: 74, source: 75);

        var result = await Controller().Purge(new PurgeRequest([74, 75]), CancellationToken.None);

        Assert.Equal([75], GetValue<List<int>>(result, "mergeSourceIds"));
        Assert.Equal(2, await _context.Activities.CountAsync());   // neither row touched
    }

    /// <summary>"Empty trash" is the highest-stakes caller of the same gate — it must refuse exactly like a
    /// targeted purge when the trash it would empty contains a live merge source, and leave EVERYTHING
    /// untouched (not just the flagged row) until confirmed.</summary>
    [Fact]
    public async Task EmptyTrash_RefusesWhenTheTrashContainsALiveMergeSource()
    {
        await SeedLiveMergeAsync(product: 80, source: 81);   // product stays released, source trashed
        await _command.DeleteActivityAsync(await AddRideAsync("Plain Trash", new TrackId(82)));

        var result = await Controller().Empty(new PurgeRequest(null), CancellationToken.None);

        Assert.Equal([81], GetValue<List<int>>(result, "mergeSourceIds"));
        Assert.Equal(2, await _trash.ListAsync().ContinueWith(t => t.Result.Count));   // nothing purged
    }

    /// <summary>Once confirmed, emptying the trash purges the merge source AND every plain trashed row in the
    /// same pass — confirmation is not scoped to just the flagged id.</summary>
    [Fact]
    public async Task EmptyTrash_WithConfirmation_PurgesTheMergeSourceAndEverythingElse()
    {
        await SeedLiveMergeAsync(product: 83, source: 84);
        await _command.DeleteActivityAsync(await AddRideAsync("Plain Trash", new TrackId(85)));

        var result = await Controller().Empty(
            new PurgeRequest(null, ConfirmMergeSources: true), CancellationToken.None);

        Assert.Equal(2, Purged(result));
        Assert.Empty(await _trash.ListAsync());
        // The product (never trashed) is untouched — only the trash was emptied.
        Assert.Equal(1, await _context.Activities.CountAsync(a => a.TrackId == new TrackId(83)));
    }

    /// <summary>A merge source that is trashed but whose product has ALREADY been purged needs no
    /// confirmation — <see cref="PurgingTheMergeProduct_UnSupersedesItsSources_SoTheyArePlainTrashAgain"/>
    /// covers the FK's SET NULL; this pins that the purge endpoint itself then lets it through cleanly.</summary>
    [Fact]
    public async Task Purge_OfAMergeSource_WhoseProductIsAlreadyGone_NeedsNoConfirmation()
    {
        await SeedLiveMergeAsync(product: 86, source: 87);
        Assert.True(await _command.DeleteActivityCoreAsync(new TrackId(86), Me));   // product purged: FK SET NULL fires

        var result = await Controller().Purge(new PurgeRequest([87]), CancellationToken.None);

        Assert.Equal(1, Purged(result));
        Assert.Empty(await _trash.ListAsync());
    }

    [Fact]
    public async Task IsTrashed_IsFalseForSomeoneElsesTrashedRide()
    {
        _context.Users.Add(new AppUser
        {
            UserId = "other", DisplayName = "other", Email = "other@test.com", IsAdmin = false,
            CreatedAt = DateTime.UtcNow.ToString("O"), LastLogin = DateTime.UtcNow.ToString("O"),
        });
        _context.Activities.Add(new Activity
        {
            TrackId = new TrackId(32), UserId = "other", TrackName = "Not Yours", ActivityType = "Ride",
            Lifecycle = LifecycleStates.Trashed, LifecyclePrev = LifecycleStates.Released,
        });
        await _context.SaveChangesAsync();

        Assert.False(await _trash.IsTrashedAsync(new TrackId(32)));
        Assert.Empty(await _trash.ListAsync());
    }
}
