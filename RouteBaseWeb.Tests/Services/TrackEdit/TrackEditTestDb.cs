using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sync;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.TrackEdit;
using RouteBaseWeb.Services.Tracks;
using RouteBaseWeb.Tests.Helpers;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RouteBaseWeb.Services.Lifecycle;

namespace RouteBaseWeb.Tests.Services.TrackEdit;

/// <summary>
/// Shared in-memory-SQLite harness for the TrackEdit session/controller tests: one shared-cache DB with the
/// TrackEdit + content/version + staging + bbox tables, seeded Users, and a decode-only
/// <see cref="ITrackReader"/> over the real <see cref="TrackContentStore"/>. Mirrors the
/// ReviewApiControllerTests fixture style.
/// </summary>
public sealed class TrackEditTestDb : IAsyncDisposable
{
    public RouteBaseDbContext Context { get; }
    public EditSessionStore Sessions { get; }
    public ITrackObjectStore Objects { get; }
    public TrackContentStore Content { get; }
    public RecordingStagingStore Staging { get; }

    private TrackEditTestDb(RouteBaseDbContext context)
    {
        Context = context;
        Sessions = new EditSessionStore(context);
        Objects = new TrackObjectStore(context, TimeProvider.System);
        Content = new TrackContentStore(context, Objects, TimeProvider.System);
        Staging = new RecordingStagingStore(context);
    }

    public static async Task<TrackEditTestDb> CreateAsync(params string[] users)
    {
        var conn = $"Data Source=file:trackedit_svc_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        var context = new RouteBaseDbContext(options);
        await context.Database.OpenConnectionAsync();
        await context.Database.EnsureCreatedAsync();
        var schema = new DbSchemaManager(context);
        await schema.EnsureRecordingStagingTableAsync();
        await schema.EnsureActivityBboxTableAsync();
        await schema.EnsureTrackEditTablesAsync();
        await schema.EnsureTrackContentTablesAsync();

        foreach (var id in users.Length == 0 ? new[] { "me" } : users)
            context.Users.Add(new AppUser { UserId = id, DisplayName = id, Email = $"{id}@t.com", IsAdmin = false });
        await context.SaveChangesAsync();
        return new TrackEditTestDb(context);
    }

    /// <summary>Shared queue so save/revert tests can read back the enqueued derive item.</summary>
    public OperationQueue Queue { get; } = new(TimeProvider.System);

    /// <summary><paramref name="reader"/> overrides the decode-only reader — pass <see cref="ThrowingReader"/>
    /// to prove a path touches no track blob (TE-01).</summary>
    public EditSessionService ServiceFor(string userId, ITrackReader? reader = null) => new(
        Sessions, Content, Staging, reader ?? new DecodingReader(Content), Context, new TestVisibilityService(userId),
        TimeProvider.System);

    /// <summary>Any blob read is a test failure — the read paths must run on stamped counts alone (TE-01).</summary>
    public sealed class ThrowingReader : ITrackReader
    {
        public Task<DecodedTrack?> GetTrackAsync(TrackId trackId, CancellationToken ct = default) =>
            throw new InvalidOperationException($"read path decoded track {trackId} — it must not touch blobs");
    }

    public EditSuggestionService SuggestionServiceFor(string userId) => new(
        Context, new TestVisibilityService(userId), Sessions);

    public TrackEditSaveService SaveServiceFor(string userId)
    {
        var vis = new TestVisibilityService(userId);
        var invalidator = new UserCacheInvalidator();
        var import = new ActivityImportService(Context, Content, NullLogger<ActivityImportService>.Instance);
        var command = new ActivityCommandService(
            Context, vis, invalidator, NullLogger<ActivityCommandService>.Instance,
            TestLifecycle.For(Context, vis, invalidator), Objects);
        return new TrackEditSaveService(
            Sessions, new TrackEditCommitStore(Context), Content,
            Staging, import, command, new NullTimezoneResolver(), new GpsNormalizationService(), Context, vis,
            invalidator, Queue, NullLogger<TrackEditSaveService>.Instance, TimeProvider.System);
    }

    public TrackEditRevertService RevertServiceFor(string userId, ISegmentAnalysis? segments = null)
    {
        var vis = new TestVisibilityService(userId);
        var invalidator = new UserCacheInvalidator();
        var command = new ActivityCommandService(
            Context, vis, invalidator, NullLogger<ActivityCommandService>.Instance,
            TestLifecycle.For(Context, vis, invalidator), Objects);
        return new TrackEditRevertService(
            new TrackEditCommitStore(Context), Content, Staging,
            command, segments ?? new FakeSegmentAnalysis(), new NullTimezoneResolver(), new GpsNormalizationService(),
            Context, vis, invalidator, Queue, TimeProvider.System);
    }

    /// <summary>Consume one enqueued work item (blocks until one is present — tests always enqueue first).</summary>
    public async Task<OperationQueue.WorkItem> DequeueOneAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var item in Queue.ReadAllAsync(cts.Token)) return item;
        throw new InvalidOperationException("queue empty");
    }

    /// <summary>Version history over this DB, owner-scoped to <paramref name="userId"/>. Pass
    /// <paramref name="deriveLogger"/> to capture the queue-full derive-drop warning — it is emitted by the
    /// shared <see cref="TrackDeriveQueue"/> seam, not by the history itself; defaults to the null logger so
    /// the existing one-arg callers are unaffected.</summary>
    public RouteBaseWeb.Services.Tracks.TrackVersionHistory HistoryFor(
        string userId, ILogger<TrackDeriveQueue>? deriveLogger = null) =>
        new(Context, Content, new TestVisibilityService(userId),
            new GpsNormalizationService(), new NullTimezoneResolver(), Staging,
            new TrackDeriveQueue(Queue, deriveLogger ?? NullLogger<TrackDeriveQueue>.Instance), Objects,
            new UserCacheInvalidator(),
            NullLogger<RouteBaseWeb.Services.Tracks.TrackVersionHistory>.Instance);

    /// <summary>How many distinct content objects exist — proves "restore costs zero new bytes".</summary>
    public async Task<int> ObjectCountAsync() =>
        await Context.Database.SqlQueryRaw<int>("SELECT COUNT(*) AS Value FROM Track_Object").SingleAsync();

    public Task<Activity?> ActivityAsync(TrackId trackId) =>
        Context.Activities.AsNoTracking().FirstOrDefaultAsync(a => a.TrackId == trackId);

    /// <summary>Session ids a user owns, newest-updated first — the persistence oracle for "the rolled-back
    /// session left no row". Lives here, not on EditSessionStore: no product surface lists sessions (the
    /// endpoint died with F-1), so the query would be production code with only test callers.</summary>
    public async Task<IReadOnlyList<string>> SessionIdsForAsync(string userId) =>
        await Context.Database
            .SqlQueryRaw<string>(
                "SELECT session_id AS Value FROM Activity_Edit_Session WHERE user_id = {0} ORDER BY updated_at DESC;",
                userId)
            .ToListAsync();

    /// <summary>Commit ids a user owns, newest first — the persistence oracle for "the save wrote a commit
    /// row" (or, on a dry-run/failed save, wrote none). Lives here, not on TrackEditCommitStore: the
    /// production list-by-user method (ListForUserAsync) was superseded by NewestOtherCommitReplacingAsync
    /// and had only test callers, so the raw query is a test oracle now (DC29-R1, DC-2 pattern).</summary>
    public async Task<IReadOnlyList<int>> CommitIdsForAsync(string userId) =>
        await Context.Database
            .SqlQueryRaw<int>(
                "SELECT commit_id AS Value FROM Track_Edit_Commit WHERE user_id = {0} ORDER BY committed_at DESC, commit_id DESC;",
                userId)
            .ToListAsync();

    /// <summary>Delete an activity PERMANENTLY — what "empty trash" does. Used to prove that losing a merge's
    /// input is what blocks the un-merge (invariant 4).</summary>
    public async Task PurgeActivityAsync(TrackId trackId)
    {
        Context.ChangeTracker.Clear();
        foreach (var child in RouteBaseWeb.Services.Sql.ChildTableRegistry.TrackChildTables)
            await Context.Database.ExecuteSqlRawAsync(child.DeleteByKeySql, trackId.Value);
        await Context.Database.ExecuteSqlRawAsync("DELETE FROM Activity WHERE track_ID = {0};", trackId.Value);
    }

    /// <summary>Seed an Activity_Bbox row (imports write one; TrackSeed only writes the blob).</summary>
    public Task SeedBboxAsync(TrackId trackId, double minLat, double maxLat, double minLng, double maxLng) =>
        Context.Database.ExecuteSqlRawAsync(
            RouteBaseWeb.Services.Sql.ActivityEditSql.RewriteActivityBboxSql, trackId.Value, minLat, maxLat, minLng, maxLng);

    public async Task<(double MinLat, double MaxLat, double MinLng, double MaxLng)?> BboxAsync(TrackId trackId)
    {
        await Context.Database.OpenConnectionAsync();
        try
        {
            var conn = (SqliteConnection)Context.Database.GetDbConnection();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT min_lat, max_lat, min_long, max_long FROM Activity_Bbox WHERE track_ID = $t;";
            var p = cmd.CreateParameter(); p.ParameterName = "$t"; p.Value = trackId.Value; cmd.Parameters.Add(p);
            await using var r = await cmd.ExecuteReaderAsync();
            return await r.ReadAsync()
                ? (r.GetDouble(0), r.GetDouble(1), r.GetDouble(2), r.GetDouble(3))
                : null;
        }
        finally { await Context.Database.CloseConnectionAsync(); }
    }

    private sealed class NullTimezoneResolver : IActivityTimezoneResolver
    {
        public Task<TimeSpan?> ResolveOffsetAsync(double? lat, double? lng, DateTime startUtc, string userId) =>
            Task.FromResult<TimeSpan?>(null);
    }

    public async Task AddActivityAsync(
        TrackId trackId, string owner, string lifecycle = LifecycleStates.Released, string? name = null,
        float? tzHours = null, string source = "manual")
    {
        Context.Activities.Add(new Activity
        {
            TrackId = trackId, UserId = owner, ActivityType = "Ride",
            Source = source, Lifecycle = lifecycle, Visibility = "private",
            TrackName = name ?? $"Track {trackId}", DateTimeTimezone = tzHours,
        });
        await Context.SaveChangesAsync();
    }

    /// <summary>Seed a scalar-only Activity for the suggestion tests (no blob — suggestions read scalars only).
    /// <paramref name="name"/> defaults to null so the fallback-name path can be exercised.</summary>
    public async Task AddSuggestibleAsync(
        TrackId trackId, string owner, string? name = null, string lifecycle = LifecycleStates.Released,
        double? startLat = null, double? startLng = null, DateTime? startLocal = null,
        double? distanceMeters = null, string? activityType = "Ride")
    {
        Context.Activities.Add(new Activity
        {
            TrackId = trackId, UserId = owner, ActivityType = activityType,
            Source = "manual", Lifecycle = lifecycle, Visibility = "private",
            TrackName = name, StartGpsLat = startLat, StartGpsLong = startLng,
            StartDateTimeLocal = startLocal, StartDateTimeUtc = startLocal, TotalDistance = distanceMeters,
        });
        await Context.SaveChangesAsync();
    }

    /// <summary>Seed a simple ascending lat/lng/time ramp of <paramref name="count"/> points.</summary>
    public Task SeedRampAsync(TrackId trackId, int count)
    {
        var start = new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Utc);
        var points = Enumerable.Range(0, count).Select(i => new GpsData
        {
            GpsLat = 47.0 + i * 0.001, GpsLong = 11.0, DateTimeUtc = start.AddSeconds(i),
        }).ToList();
        return TrackSeed.SeedTrackAsync(Context, trackId, points);
    }

    public Task SeedBlobAsync(TrackId trackId, IReadOnlyList<GpsData> points) =>
        TrackSeed.SeedTrackAsync(Context, trackId, points);

    /// <summary>The bytes the track currently IS (its head version's object).</summary>
    public Task<byte[]?> HeadBytesAsync(TrackId trackId) => Content.GetHeadDataAsync(trackId);

    /// <summary>The track's revision list, oldest first — what used to be "the blob plus its snapshots".</summary>
    public Task<IReadOnlyList<TrackVersionRow>> VersionsAsync(TrackId trackId) => Content.ListVersionsAsync(trackId);

    /// <summary>Strip a track's content entirely, as a permanent purge would (the head flag dies with the
    /// version rows). Replaces the old "delete the blob row" fixture move.</summary>
    public async Task RemoveContentAsync(TrackId trackId)
    {
        await Context.Database.ExecuteSqlRawAsync("DELETE FROM Activity_Version WHERE track_id = {0};", trackId.Value);
    }

    /// <summary>Append a different-geometry version → the session's stamp of the old head goes stale.</summary>
    public Task MutateBlobAsync(TrackId trackId) => SeedRampAsync(trackId, 7);

    public Task AddStagingAsync(TrackId trackId) => Staging.AppendAsync(
        trackId, 0, new[] { new ExternalGpsPoint { Lat = 47.5, Lng = 11.5, TimestampUtc = DateTime.UtcNow } });

    public async ValueTask DisposeAsync()
    {
        await Context.Database.CloseConnectionAsync();
        await Context.DisposeAsync();
    }

    /// <summary>Decode-only reader over the track's current content (no cache/staging merge) — the session
    /// validation replay input; sources are guaranteed staging-free (the guard runs before create/append).</summary>
    private sealed class DecodingReader(TrackContentStore content) : ITrackReader
    {
        public async Task<DecodedTrack?> GetTrackAsync(TrackId trackId, CancellationToken ct = default)
        {
            var bytes = await content.GetHeadDataAsync(trackId, ct);
            return bytes is null ? null : TrackBlobCodec.Decode(bytes);
        }
    }
}
