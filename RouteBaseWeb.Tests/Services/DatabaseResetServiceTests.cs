using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Heatmaps;
using RouteBaseWeb.Tests.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Characterization tests for <see cref="DatabaseResetService"/> — per-user scoped deletion.
/// Pins that resetting activities also clears the user's RecordingSession rows (ME-01), now via the
/// Activity→RecordingSession FK cascade: reset runs with foreign keys enforced, in one transaction.
/// Named in-memory SQLite (shared cache) so the raw-SQL batch + PRAGMA sequence run for real.
/// </summary>
public class DatabaseResetServiceTests : IAsyncLifetime
{
    private const string Me = "me";
    private RouteBaseDbContext _context = null!;
    private DatabaseResetService _service = null!;
    private HeatmapTilePackStore _tileStore = null!;
    private string _tileRoot = null!;

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:reset_test_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.EnsureCreatedAsync();
        // The reset now deletes from the non-EF track point stores; they must exist.
        var schema = new DbSchemaManager(_context);
        await schema.EnsureTrackContentTablesAsync();
        await schema.EnsureRecordingStagingTableAsync();
        // EditCore (A1 §2.8): reset also wipes Track_Blob_History/Activity_Edit_Session/Track_Edit_Commit.
        await schema.EnsureTrackEditTablesAsync();
        await schema.EnsureErrorLogTableAsync(); // B8: every reset also wipes the caller's Error_Log rows
        _context.Users.Add(new AppUser { UserId = Me, DisplayName = Me, Email = "me@t.com", IsAdmin = false });
        await _context.SaveChangesAsync();

        _tileRoot = Path.Combine(Path.GetTempPath(), $"hmreset_{Guid.NewGuid():N}");
        _tileStore = new HeatmapTilePackStore(
            new HeatmapStorageOptions { Root = _tileRoot },
            new HeatmapTileIndexCache(new MemoryCache(new MemoryCacheOptions())), TimeProvider.System);

        // Purge routes through IHeatmapLifecycle.PurgeTiles (arch finding D — one door, shared with
        // SegmentCrud's segment-delete purge), not HeatmapTilePackStore directly.
        var heatmapLifecycle = new HeatmapLifecycle(
            _context, new TestVisibilityService(Me), _tileStore, NullLogger<HeatmapLifecycle>.Instance);
        _service = new DatabaseResetService(
            _context, NullLogger<DatabaseResetService>.Instance, new TestCurrentUserService(Me),
            heatmaps: heatmapLifecycle);
    }

    public async Task DisposeAsync()
    {
        await _context.DisposeAsync();
        try { Directory.Delete(_tileRoot, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    // Persist a one-tile packed file for a heatmap so we can assert it's purged (or kept) after a reset.
    private async Task WriteTileFileAsync(int heatmapId)
    {
        await using var writer = _tileStore.BeginWrite(heatmapId);
        await writer.AppendAsync(HeatmapGenerationService.CountLogLayer,
            new[] { new HeatmapTileData(0, 0, 0, new byte[] { 1, 2, 3 }) });
        await writer.CommitAsync();
    }

    private bool TileFileExists(int heatmapId) => File.Exists(_tileStore.GetPhysicalPath(heatmapId));

    private async Task<bool> BlobExistsAsync(TrackId trackId)
    {
        var rows = await _context.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Activity_Version WHERE track_id = {0} AND is_head = 1", trackId.Value)
            .ToListAsync();
        return rows.FirstOrDefault() > 0;
    }

    [Fact]
    public async Task ResetActivities_DeletesUsersRecordingSessions()
    {
        _context.Activities.Add(new Activity { TrackId = new TrackId(1), UserId = Me, Visibility = "private" });
        await _context.SaveChangesAsync();
        _context.RecordingSessions.Add(new RecordingSession
        {
            SessionId = Guid.NewGuid().ToString(), TrackId = new TrackId(1), UserId = Me,
            Status = "recording", StartedAt = DateTime.UtcNow,
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await _service.ResetDatabaseAsync(new DeleteOptions { Activities = true });

        Assert.False(await _context.RecordingSessions.AnyAsync(s => s.UserId == Me));
        Assert.False(await _context.Activities.AnyAsync(a => a.UserId == Me));
    }

    /// <summary>
    /// A race whose course is a FRIEND's activity does not die with the user's own Activity rows —
    /// the Race.track_id cascade only reaches races built on own rides. The reset must wipe races by
    /// user_id directly (the ChildTableRegistry.RacesByUser entry's own documented intent; idempotent
    /// double-delete with the cascade). Cascades then take the participants/checkpoints/splits.
    /// </summary>
    [Fact]
    public async Task ResetActivities_DeletesRaceBuiltOnFriendsCourse()
    {
        const string friend = "friend";
        _context.Users.Add(new AppUser { UserId = friend, DisplayName = friend, Email = "f@t.com", IsAdmin = false });
        _context.Activities.Add(new Activity { TrackId = new TrackId(7), UserId = friend, Visibility = "friends" });
        await _context.SaveChangesAsync();
        _context.Races.Add(new Race { RaceId = new RaceId(1), RaceName = "Duplicated race", CourseTrackId = new TrackId(7), UserId = Me });
        await _context.SaveChangesAsync();
        _context.RaceParticipants.Add(new RaceParticipant { ParticipantId = new ParticipantId(1), RaceId = new RaceId(1) });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await _service.ResetDatabaseAsync(new DeleteOptions { Activities = true });

        Assert.False(await _context.Races.AnyAsync(r => r.UserId == Me));
        Assert.False(await _context.RaceParticipants.AnyAsync(p => p.RaceId == new RaceId(1)));
        Assert.True(await _context.Activities.AnyAsync(a => a.UserId == friend)); // course survives
    }

    /// <summary>
    /// RM-T1: a full reset clears every user-keyed table for the current user and leaves other users
    /// untouched. Green since the D2 reset rework (RM-02/03/04/08): reset runs cascade-driven with
    /// foreign keys enforced, so Segments/Activities branches also clean their cascade children, and
    /// Photos are deleted on the standard full reset (previously INC-01 + RM-04 gaps).
    /// </summary>
    [Fact]
    public async Task ResetAll_ClearsEveryUserKeyedTable_AndKeepsOtherUsers()
    {
        const string other = "other";

        // Activity_Bbox is not in the EF model — mirror Program.cs and create it explicitly.
        var schema = new DbSchemaManager(_context);
        await schema.EnsureActivityBboxTableAsync();

        _context.Users.Add(new AppUser { UserId = other, DisplayName = other, Email = "b@t.com", IsAdmin = false });

        // ── User A (Me): every user-keyed table + its cascade children ──
        _context.MultiDayTours.Add(new MultiDayTour { MultiDayId = 10, UserId = Me });
        _context.Activities.Add(new Activity { TrackId = new TrackId(1), UserId = Me, Visibility = "private", MultiDayId = 10 });
        _context.Photos.Add(new Photo { PhotoId = new PhotoId(1), TrackId = new TrackId(1), UserId = Me, ImportedUtc = DateTime.UtcNow });
        _context.RecordingSessions.Add(new RecordingSession
        {
            SessionId = Guid.NewGuid().ToString(), TrackId = new TrackId(1), UserId = Me,
            Status = "recording", StartedAt = DateTime.UtcNow,
        });
        _context.Segments.Add(new Segment { SegmentId = new SegmentId(1), UserId = Me });
        _context.SegmentGps.Add(new SegmentGps { SegGpsId = 1, SegmentId = new SegmentId(1), GpsLat = 1, GpsLong = 1 });
        _context.Heatmaps.Add(new Heatmap { HeatmapId = 1, SegmentId = new SegmentId(1), Status = "done" });
        _context.GpsInSegments.Add(new GpsInSegment { GpsInSegmentId = new GpsInSegmentId(1), TrackId = new TrackId(1), SegmentId = new SegmentId(1) });
        _context.AppCredentials.Add(new AppCredentials { Id = 1, Type = CredentialTypes.StravaToken, UserId = Me });
        _context.ProfileSettings.Add(new ProfileSettings
        {
            Id = 1, Category = "Charts", SettingKey = "K", SettingValue = "V", UserId = Me,
        });
        // Share links on both resource kinds. The registry (ShareLinksByUser) declares "a reset that
        // left live tokens behind would be a data-exposure bug"; today the resource cascades are what
        // deliver that — this pins the OUTCOME so a schema/reset change that breaks it goes red (DM-03).
        _context.ShareLinks.Add(new ShareLink
        {
            ShareLinkId = 1, TokenHash = "th-a", Kind = ShareKindNames.Activity, TrackId = new TrackId(1), UserId = Me,
            CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(7),
        });
        _context.ShareLinks.Add(new ShareLink
        {
            ShareLinkId = 2, TokenHash = "th-s", Kind = ShareKindNames.Segment, SegmentId = new SegmentId(1), UserId = Me,
            CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(7),
        });

        // ── User B (other): a slim set that must survive A's reset ──
        _context.Activities.Add(new Activity { TrackId = new TrackId(2), UserId = other, Visibility = "private" });
        _context.Segments.Add(new Segment { SegmentId = new SegmentId(2), UserId = other });
        _context.GpsInSegments.Add(new GpsInSegment { GpsInSegmentId = new GpsInSegmentId(2), TrackId = new TrackId(2), SegmentId = new SegmentId(2) });
        _context.Heatmaps.Add(new Heatmap { HeatmapId = 2, SegmentId = new SegmentId(2), Status = "done" });
        _context.ShareLinks.Add(new ShareLink
        {
            ShareLinkId = 3, TokenHash = "th-b", Kind = ShareKindNames.Activity, TrackId = new TrackId(2), UserId = other,
            CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(7),
        });
        await _context.SaveChangesAsync();

        // Heatmap tile files now live on the filesystem — one packed file per heatmap.
        await WriteTileFileAsync(1);   // User A's — must be purged
        await WriteTileFileAsync(2);   // User B's — must survive

        // Track 1's points live in Track_Blob (was a GPS row).
        await TrackSeed.SeedTrackAsync(_context, new TrackId(1), new[] { new GpsData { GpsLat = 1, GpsLong = 1 } });

        // Activity_Bbox rows (raw — not an EF entity).
        await _context.Database.ExecuteSqlRawAsync(
            "INSERT INTO Activity_Bbox (track_ID, min_lat, max_lat, min_long, max_long) VALUES (1, 0, 1, 0, 1), (2, 0, 1, 0, 1);");
        _context.ChangeTracker.Clear();

        await _service.ResetDatabaseAsync(new DeleteOptions
        {
            Activities = true, Gps = true, Photos = true, MultiDayTours = true,
            SegmentMatches = true, Segments = true, ProfileSettings = true,
            StravaToken = true, StravaApiKey = true, MapTilerApiKey = true,
        });
        _context.ChangeTracker.Clear();

        // User A: nothing left anywhere.
        Assert.False(await _context.Activities.AnyAsync(a => a.UserId == Me));
        Assert.False(await BlobExistsAsync(new TrackId(1)));
        Assert.False(await _context.Photos.AnyAsync(p => p.TrackId == new TrackId(1)));
        Assert.False(await _context.RecordingSessions.AnyAsync(s => s.UserId == Me));
        Assert.False(await _context.MultiDayTours.AnyAsync(t => t.UserId == Me));
        Assert.False(await _context.Segments.AnyAsync(s => s.UserId == Me));
        Assert.False(await _context.SegmentGps.AnyAsync(g => g.SegmentId == new SegmentId(1)));
        Assert.False(await _context.Heatmaps.AnyAsync(h => h.SegmentId == new SegmentId(1)));
        Assert.False(TileFileExists(1)); // packed tile file purged after the row cascade
        Assert.False(await _context.GpsInSegments.AnyAsync(e => e.TrackId == new TrackId(1)));
        Assert.False(await _context.AppCredentials.AnyAsync(c => c.UserId == Me));
        Assert.False(await _context.ProfileSettings.AnyAsync(s => s.UserId == Me));
        Assert.False(await _context.ShareLinks.AnyAsync(l => l.UserId == Me)); // no live token survives a reset
        Assert.Equal(0, await CountBboxAsync(new TrackId(1)));

        // User B: untouched.
        Assert.True(await _context.Activities.AnyAsync(a => a.UserId == other));
        Assert.True(await _context.Segments.AnyAsync(s => s.UserId == other));
        Assert.True(await _context.GpsInSegments.AnyAsync(e => e.TrackId == new TrackId(2)));
        Assert.True(await _context.Heatmaps.AnyAsync(h => h.SegmentId == new SegmentId(2)));
        Assert.True(TileFileExists(2)); // other user's tile file untouched
        Assert.True(await _context.ShareLinks.AnyAsync(l => l.UserId == other));
        Assert.Equal(1, await CountBboxAsync(new TrackId(2)));
    }

    /// <summary>
    /// B8: reset means reset — ANY confirmed reset (here the smallest one, a token-only wipe) also
    /// drops the caller's diagnostic Error_Log rows. Other users' rows and system rows (user_id NULL)
    /// are not the caller's and must survive.
    /// </summary>
    [Fact]
    public async Task Reset_WipesOwnErrorLogRows_KeepsOtherUsersAndSystemRows()
    {
        await _context.Database.ExecuteSqlRawAsync(
            "INSERT INTO Error_Log (timestamp_utc, source, category, level, message, user_id) VALUES " +
            "('2026-07-20 00:00:01', 'app', 'Cat', 'Error', 'mine', {0})," +
            "('2026-07-20 00:00:02', 'app', 'Cat', 'Error', 'theirs', 'other')," +
            "('2026-07-20 00:00:03', 'app', 'Cat', 'Error', 'system', NULL);", Me);

        await _service.ResetDatabaseAsync(new DeleteOptions { StravaToken = true });

        var remaining = await _context.Database
            .SqlQueryRaw<string>("SELECT message AS Value FROM Error_Log ORDER BY error_id")
            .ToListAsync();
        Assert.Equal(new[] { "theirs", "system" }, remaining, StringComparer.Ordinal);
    }

    // ── RM-03: GPSinSegment OR-predicate — cross-user efforts ──────────────────

    [Fact]
    public async Task ResetActivities_DeletesOwnEffortsOnFriendSegments()
    {
        const string friend = "friend";
        _context.Users.Add(new AppUser { UserId = friend, DisplayName = friend, Email = "f@t.com", IsAdmin = false });
        _context.Activities.Add(new Activity { TrackId = new TrackId(1), UserId = Me, Visibility = "private" });
        _context.Activities.Add(new Activity { TrackId = new TrackId(2), UserId = friend, Visibility = "private" });
        _context.Segments.Add(new Segment { SegmentId = new SegmentId(5), UserId = friend });
        _context.GpsInSegments.Add(new GpsInSegment { GpsInSegmentId = new GpsInSegmentId(1), TrackId = new TrackId(1), SegmentId = new SegmentId(5) }); // my effort on friend's segment
        _context.GpsInSegments.Add(new GpsInSegment { GpsInSegmentId = new GpsInSegmentId(2), TrackId = new TrackId(2), SegmentId = new SegmentId(5) }); // friend's effort on own segment
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await _service.ResetDatabaseAsync(new DeleteOptions { Activities = true });
        _context.ChangeTracker.Clear();

        Assert.False(await _context.GpsInSegments.AnyAsync(e => e.GpsInSegmentId == new GpsInSegmentId(1)));
        Assert.True(await _context.GpsInSegments.AnyAsync(e => e.GpsInSegmentId == new GpsInSegmentId(2)));
    }

    [Fact]
    public async Task ResetSegments_DeletesFriendEffortsOnOwnSegments()
    {
        const string friend = "friend";
        _context.Users.Add(new AppUser { UserId = friend, DisplayName = friend, Email = "f@t.com", IsAdmin = false });
        _context.Segments.Add(new Segment { SegmentId = new SegmentId(5), UserId = Me });
        _context.Segments.Add(new Segment { SegmentId = new SegmentId(6), UserId = friend });
        _context.Activities.Add(new Activity { TrackId = new TrackId(2), UserId = friend, Visibility = "private" });
        _context.GpsInSegments.Add(new GpsInSegment { GpsInSegmentId = new GpsInSegmentId(1), TrackId = new TrackId(2), SegmentId = new SegmentId(5) }); // friend's effort on my segment
        _context.GpsInSegments.Add(new GpsInSegment { GpsInSegmentId = new GpsInSegmentId(2), TrackId = new TrackId(2), SegmentId = new SegmentId(6) }); // friend's effort on own segment
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await _service.ResetDatabaseAsync(new DeleteOptions { Segments = true });
        _context.ChangeTracker.Clear();

        Assert.False(await _context.GpsInSegments.AnyAsync(e => e.GpsInSegmentId == new GpsInSegmentId(1)));
        Assert.True(await _context.GpsInSegments.AnyAsync(e => e.GpsInSegmentId == new GpsInSegmentId(2)));
    }

    // ── RM-04: Photos deleted on Activities and Gps paths (scoped to the user's tracks) ─────────

    [Fact]
    public async Task ResetActivities_DeletesPhotos()
    {
        const string other = "other";
        _context.Users.Add(new AppUser { UserId = other, DisplayName = other, Email = "o@t.com", IsAdmin = false });
        _context.Activities.Add(new Activity { TrackId = new TrackId(1), UserId = Me, Visibility = "private" });
        _context.Activities.Add(new Activity { TrackId = new TrackId(2), UserId = other, Visibility = "private" });
        _context.Photos.Add(new Photo { PhotoId = new PhotoId(1), TrackId = new TrackId(1), UserId = Me, ImportedUtc = DateTime.UtcNow });
        _context.Photos.Add(new Photo { PhotoId = new PhotoId(2), TrackId = new TrackId(2), UserId = other, ImportedUtc = DateTime.UtcNow });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await _service.ResetDatabaseAsync(new DeleteOptions { Activities = true, Photos = false });
        _context.ChangeTracker.Clear();

        Assert.False(await _context.Photos.AnyAsync(p => p.TrackId == new TrackId(1))); // my track's photo gone
        Assert.True(await _context.Photos.AnyAsync(p => p.TrackId == new TrackId(2)));  // other user's untouched
    }

    [Fact]
    public async Task ResetGpsAndPhotos_DeletesPhotos()
    {
        const string other = "other";
        _context.Users.Add(new AppUser { UserId = other, DisplayName = other, Email = "o@t.com", IsAdmin = false });
        _context.Activities.Add(new Activity { TrackId = new TrackId(1), UserId = Me, Visibility = "private" });
        _context.Activities.Add(new Activity { TrackId = new TrackId(2), UserId = other, Visibility = "private" });
        _context.Photos.Add(new Photo { PhotoId = new PhotoId(1), TrackId = new TrackId(1), UserId = Me, ImportedUtc = DateTime.UtcNow });
        _context.Photos.Add(new Photo { PhotoId = new PhotoId(2), TrackId = new TrackId(2), UserId = other, ImportedUtc = DateTime.UtcNow });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await _service.ResetDatabaseAsync(new DeleteOptions { Gps = true, Photos = true });
        _context.ChangeTracker.Clear();

        Assert.False(await _context.Photos.AnyAsync(p => p.TrackId == new TrackId(1)));
        Assert.True(await _context.Photos.AnyAsync(p => p.TrackId == new TrackId(2)));
    }

    // ── RM-08: Gps-only reset also drops the efforts derived from those points ──

    [Fact]
    public async Task ResetGps_DeletesOwnEfforts()
    {
        _context.Activities.Add(new Activity { TrackId = new TrackId(1), UserId = Me, Visibility = "private" });
        _context.Segments.Add(new Segment { SegmentId = new SegmentId(5), UserId = Me });
        _context.GpsInSegments.Add(new GpsInSegment { GpsInSegmentId = new GpsInSegmentId(1), TrackId = new TrackId(1), SegmentId = new SegmentId(5) });
        await _context.SaveChangesAsync();
        await TrackSeed.SeedTrackAsync(_context, new TrackId(1), new[] { new GpsData { GpsLat = 1, GpsLong = 1 } });
        _context.ChangeTracker.Clear();

        await _service.ResetDatabaseAsync(new DeleteOptions { Gps = true });
        _context.ChangeTracker.Clear();

        Assert.False(await _context.GpsInSegments.AnyAsync(e => e.TrackId == new TrackId(1)));
        Assert.False(await BlobExistsAsync(new TrackId(1)));
        Assert.True(await _context.Activities.AnyAsync(a => a.TrackId == new TrackId(1))); // Activity retained (Gps-only)
    }

    // ── RM-02: single transaction — a mid-batch failure rolls back the whole reset ──

    [Fact]
    public async Task Reset_MidBatchFailure_RollsBackAllDeletes()
    {
        _context.Activities.Add(new Activity { TrackId = new TrackId(1), UserId = Me, Visibility = "private" });
        _context.Photos.Add(new Photo { PhotoId = new PhotoId(1), TrackId = new TrackId(1), UserId = Me, ImportedUtc = DateTime.UtcNow });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        // Drop Activity_Version so the reset's point-store delete fails *after* the Photos delete already ran
        // in-transaction (the reset deletes Activity_Version/Recording_Staging).
        await _context.Database.ExecuteSqlRawAsync("DROP TABLE [Activity_Version];");

        await Assert.ThrowsAnyAsync<Exception>(
            () => _service.ResetDatabaseAsync(new DeleteOptions { Activities = true }));
        _context.ChangeTracker.Clear();

        // Rolled back — the earlier Photos + Activity deletes are undone.
        Assert.True(await _context.Photos.AnyAsync(p => p.TrackId == new TrackId(1)));
        Assert.True(await _context.Activities.AnyAsync(a => a.TrackId == new TrackId(1)));
    }

    /// <summary>
    /// RM-T2: pins the runtime assumption the reset redesign rests on — Microsoft.Data.Sqlite
    /// enforces foreign keys by default (no PRAGMA set here), so a DDL ON DELETE CASCADE fires.
    /// If this ever goes red, RM-02 falls back to a pinned window with an explicit foreign_keys ON.
    /// </summary>
    [Fact]
    public async Task RuntimeForeignKeys_DefaultOn_CascadeFires()
    {
        _context.Segments.Add(new Segment { SegmentId = new SegmentId(7), UserId = Me });
        _context.Heatmaps.Add(new Heatmap { HeatmapId = 7, SegmentId = new SegmentId(7), Status = "done" });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await _context.Database.ExecuteSqlRawAsync("DELETE FROM [Segment] WHERE segment_ID = 7;");
        _context.ChangeTracker.Clear();

        Assert.False(await _context.Heatmaps.AnyAsync(h => h.SegmentId == new SegmentId(7)));
    }

    /// <summary>
    /// Version_Parent is the one content-axis table with no track/segment/user/race key, so
    /// <c>ChildTableRegistry</c> and its rot-guard (ChildTableRegistryParityTests) cannot see it and
    /// no delete seam names it explicitly: purging an Activity deletes Activity_Version explicitly
    /// (ActivityVersionsByTrack in DeleteActivityCoreAsync), and Version_Parent is cleaned ONLY by the
    /// resulting <c>ON DELETE CASCADE</c> (version_id → Activity_Version). Pin that the cascade the seam
    /// leans on actually fires at runtime — a lost REFERENCES clause on that hop would silently strand
    /// lineage rows keyed by a purged version, and the generic RuntimeForeignKeys pin above
    /// (Segment→Heatmap) never touches this two-hop content axis. (parent_version_id carries NO FK and may
    /// deliberately dangle — the revert InputsPurged evidence; that separate, load-bearing design is not
    /// asserted here.)
    /// </summary>
    [Fact]
    public async Task VersionParent_CascadeCleanup_IsTheSeamsSolePurgePath()
    {
        _context.Activities.Add(new Activity { TrackId = new TrackId(1), UserId = Me, Visibility = "private" });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        await _context.Database.ExecuteSqlRawAsync(
            "INSERT INTO Track_Object (sha256, codec_version, point_count, byte_len, data, created_at) " +
            "VALUES ('sha-vp', 1, 1, 1, x'00', '2026-01-01 00:00:00');");
        // Two versions of track 1: seq-2 (child) descends from seq-1 (parent) — the lineage edge.
        await _context.Database.ExecuteSqlRawAsync(
            "INSERT INTO Activity_Version (version_id, track_id, seq, object_sha, is_head, point_count, created_at) VALUES " +
            "(101, 1, 1, 'sha-vp', 0, 1, '2026-01-01 00:00:00'), " +
            "(102, 1, 2, 'sha-vp', 1, 1, '2026-01-01 00:00:00');");
        await _context.Database.ExecuteSqlRawAsync(
            "INSERT INTO Version_Parent (version_id, parent_version_id) VALUES (102, 101);");

        // Exactly what DeleteActivityCoreAsync runs for the version table (ActivityVersionsByTrack).
        await _context.Database.ExecuteSqlRawAsync("DELETE FROM Activity_Version WHERE track_id = 1;");

        var stranded = (await _context.Database
            .SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Version_Parent").ToListAsync()).Single();
        Assert.Equal(0, stranded);   // the cascade fired — no lineage row left keyed to a purged version
    }

    [Fact]
    public async Task ResetGps_MarksAffectedDoneHeatmapsStale_KeepsTileFile()
    {
        // HM-17: a track-scoped reset wipes my effort rows everywhere; heatmaps painted from them
        // (own AND friends' segments) must flip done -> stale (imagery stays viewable, regenerable).
        const string friend = "friend";
        _context.Users.Add(new AppUser { UserId = friend, DisplayName = friend, Email = "f@t.com", IsAdmin = false });
        _context.Activities.Add(new Activity { TrackId = new TrackId(1), UserId = Me, Visibility = "private" });
        _context.Segments.Add(new Segment { SegmentId = new SegmentId(5), UserId = Me });
        _context.Segments.Add(new Segment { SegmentId = new SegmentId(6), UserId = friend });
        _context.Segments.Add(new Segment { SegmentId = new SegmentId(7), UserId = friend });
        _context.GpsInSegments.Add(new GpsInSegment { GpsInSegmentId = new GpsInSegmentId(1), TrackId = new TrackId(1), SegmentId = new SegmentId(5) }); // my effort, own segment
        _context.GpsInSegments.Add(new GpsInSegment { GpsInSegmentId = new GpsInSegmentId(2), TrackId = new TrackId(1), SegmentId = new SegmentId(6) }); // my effort, friend's segment
        _context.Heatmaps.Add(new Heatmap { HeatmapId = 11, SegmentId = new SegmentId(5), Status = "done" });
        _context.Heatmaps.Add(new Heatmap { HeatmapId = 12, SegmentId = new SegmentId(6), Status = "done" });
        _context.Heatmaps.Add(new Heatmap { HeatmapId = 13, SegmentId = new SegmentId(7), Status = "done" }); // untouched segment
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();
        await WriteTileFileAsync(11);

        await _service.ResetDatabaseAsync(new DeleteOptions { Gps = true });
        _context.ChangeTracker.Clear();

        Assert.Equal("stale", (await _context.Heatmaps.AsNoTracking().FirstAsync(h => h.HeatmapId == 11)).Status);
        Assert.Equal("stale", (await _context.Heatmaps.AsNoTracking().FirstAsync(h => h.HeatmapId == 12)).Status);
        Assert.Equal("done", (await _context.Heatmaps.AsNoTracking().FirstAsync(h => h.HeatmapId == 13)).Status);
        Assert.True(TileFileExists(11)); // stale keeps the imagery servable
    }

    private async Task<int> CountBboxAsync(TrackId trackId)
    {
        // Literal int, no injection surface — Activity_Bbox is not an EF entity so raw SQL is required.
#pragma warning disable EF1002
        return await _context.Database
            .SqlQueryRaw<int>($"SELECT COUNT(*) AS Value FROM Activity_Bbox WHERE track_ID = {trackId}")
            .FirstAsync();
#pragma warning restore EF1002
    }
}
