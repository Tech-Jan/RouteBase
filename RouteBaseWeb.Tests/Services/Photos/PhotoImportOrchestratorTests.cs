using System.Security.Cryptography;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.GridFilter;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Photos;
using RouteBaseWeb.Services.Tracks;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SkiaSharp;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Photos;

/// <summary>
/// End-to-end orchestrator coverage: unmatched (no time), persisted-hash dedup (both synthetic, always
/// run), and the full time-match import + re-import dedup path (guarded on the real WhatsApp sample).
/// </summary>
public class PhotoImportOrchestratorTests : IAsyncLifetime, IDisposable
{
    private const string Me = "me";
    // Real WhatsApp EXIF sample, opt-in via PhotoFixtures (not committed — see PhotoFixtures / REPORT).
    private static string WhatsApp => PhotoFixtures.PathOf(PhotoFixtures.WhatsAppFile);

    private RouteBaseDbContext _db = null!;
    private PhotoImportStaging _staging = null!;
    private FileSystemPhotoImageStore _store = null!;
    private PhotoImportOrchestrator _orch = null!;
    private readonly string _storeRoot =
        Path.Combine(Path.GetTempPath(), "gv-photo-orch-tests", Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:photo_orch_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _db = new RouteBaseDbContext(options);
        await _db.Database.OpenConnectionAsync(); // keep the shared-cache DB alive for the context lifetime
        await _db.Database.EnsureCreatedAsync();

        // Activity.user_id → Users.user_id is a real FK on the fresh EF schema, so the owner row must
        // exist before any Activity is seeded (production always has it from login).
        _db.Users.Add(new AppUser { UserId = Me, DisplayName = Me, Email = "me@t.com", IsAdmin = false });
        await _db.SaveChangesAsync();

        _staging = new PhotoImportStaging(NullLogger<PhotoImportStaging>.Instance, TimeProvider.System);
        _store = new FileSystemPhotoImageStore(new PhotoStorageOptions { Root = _storeRoot });
        _orch = new PhotoImportOrchestrator(
            _db, new OwnedByMeVisibility(Me), new NullActivityQuery(), new NullTrackReader(), new NullTz(),
            new PhotoExifReader(), new PhotoDerivativeService(), _store, _staging,
            NullLogger<PhotoImportOrchestrator>.Instance, TimeProvider.System);
    }

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();

    public void Dispose()
    {
        try { if (Directory.Exists(_storeRoot)) Directory.Delete(_storeRoot, true); } catch { }
    }

    // ── helpers ────────────────────────────────────────────────────────────────
    private static byte[] SyntheticJpeg(int w = 400, int h = 300, SKColor? c = null)
    {
        var info = new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Unpremul);
        using var bmp = new SKBitmap(info);
        using (var canvas = new SKCanvas(bmp)) canvas.Clear(c ?? SKColors.SlateGray);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Jpeg, 85);
        return data.ToArray();
    }

    private static FormFile FormFile(string name, byte[] bytes) =>
        new(new MemoryStream(bytes), 0, bytes.Length, "files", name);

    private async Task<(string batchId, List<PhotoFileResult> results)> RunAsync(
        int previewMaxEdge, params (string name, byte[] bytes)[] files)
    {
        var batchId = _staging.BeginBatch(Me, previewMaxEdge);
        foreach (var f in files)
            await _staging.AppendFilesAsync(Me, batchId, new[] { FormFile(f.name, f.bytes) }, default);

        var results = new List<PhotoFileResult>();
        await _orch.ImportBatchAsync(Me, batchId, _ => { }, results.Add, default);
        return (batchId, results);
    }

    // ── tests ──────────────────────────────────────────────────────────────────
    [Fact]
    public async Task NoExifNoActivities_Unmatched_NothingPersisted()
    {
        var (_, results) = await RunAsync(1024, ("a.jpg", SyntheticJpeg()));

        Assert.Single(results);
        Assert.Equal("unmatched", results[0].Outcome);
        Assert.False(await _db.Photos.AnyAsync());
    }

    [Fact]
    public async Task ForcedTrackId_ImportsDirectly_WithoutExifOrMatching()
    {
        _db.Activities.Add(new Activity { TrackId = new TrackId(5), UserId = Me, Visibility = "private", TrackName = "Trip" });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        // A synthetic no-EXIF photo would normally land "unmatched"; with a forced target it imports direct.
        var batchId = _staging.BeginBatch(Me, 1024, forceTrackId: 5);
        await _staging.AppendFilesAsync(Me, batchId, new[] { FormFile("nogps.jpg", SyntheticJpeg()) }, default);

        var results = new List<PhotoFileResult>();
        await _orch.ImportBatchAsync(Me, batchId, _ => { }, results.Add, default);

        Assert.Equal("imported", results[0].Outcome);
        var photo = await _db.Photos.SingleAsync();
        Assert.Equal(new TrackId(5), photo.TrackId);
        Assert.Equal("manual", photo.Assignment);
    }

    [Fact]
    public async Task MatchesPersistedContentHash_Duplicate()
    {
        var bytes = SyntheticJpeg(320, 240, SKColors.Teal);
#pragma warning disable CA1308 // content hash persisted as lowercase hex (PhotoImportOrchestrator) — must match to dedupe
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
#pragma warning restore CA1308

        _db.Activities.Add(new Activity { TrackId = new TrackId(1), UserId = Me, Visibility = "private" });
        _db.Photos.Add(new Photo { PhotoId = new PhotoId(1), TrackId = new TrackId(1), UserId = Me, ContentHash = hash, ImportedUtc = DateTime.UtcNow });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var (_, results) = await RunAsync(1024, ("dup.jpg", bytes));

        Assert.Equal("duplicate", results[0].Outcome);
        Assert.Equal(1, await _db.Photos.CountAsync()); // not re-imported
    }

    [Fact]
    public async Task RealWhatsApp_TimeMatch_ImportsStoresFiles_AndDedupesOnReimport()
    {
        if (!File.Exists(WhatsApp)) return; // untracked sample absent — skip

        var bytes = await File.ReadAllBytesAsync(WhatsApp);
        PhotoExif exif;
        using (var ms = new MemoryStream(bytes)) exif = new PhotoExifReader().Read(ms);
        Assert.True(exif.HasTime && exif.UtcOffset is not null);

        var capturedUtc = DateTime.SpecifyKind(exif.DateTimeOriginalLocal!.Value - exif.UtcOffset!.Value, DateTimeKind.Utc);
        // Activity spanning [captured-1h, captured+1h].
        _db.Activities.Add(new Activity
        {
            TrackId = new TrackId(7), UserId = Me, Visibility = "private", TrackName = "Evening ride",
            StartDateTimeUtc = capturedUtc.AddHours(-1), TimeElapsed = 7200,
        });
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear();

        var (_, results) = await RunAsync(1024, ("IMG-20260627-WA0003.jpeg", bytes));

        Assert.Equal("imported", results[0].Outcome);
        var photo = await _db.Photos.SingleAsync();
        Assert.Equal(new TrackId(7), photo.TrackId);
        Assert.Equal("auto", photo.Assignment);
        Assert.True(photo.HasExifTime);
        Assert.NotEqual("exif", photo.LocationSource, StringComparer.Ordinal); // WhatsApp has no EXIF GPS (null-track stub → "none")
        Assert.Equal(1024, Math.Max(photo.Width, photo.Height));
        Assert.NotNull(photo.Thumbhash);

        // Files landed in the store.
        await using (var thumb = _store.OpenRead(photo.PhotoId, PhotoTier.Thumb)) Assert.NotNull(thumb);
        await using (var prev = _store.OpenRead(photo.PhotoId, PhotoTier.Preview)) Assert.NotNull(prev);

        // Re-import the exact bytes → duplicate (SHA-256), still one row.
        var (_, again) = await RunAsync(1024, ("IMG-20260627-WA0003.jpeg", bytes));
        Assert.Equal("duplicate", again[0].Outcome);
        Assert.Equal(1, await _db.Photos.CountAsync());
    }

    // ── RefineGpsOffsetAsync (MP-01: two-pass DST-transition-safe offset resolution) ──
    [Fact]
    public async Task RefineGpsOffsetAsync_DstBoundary_ConvergesToInstantCorrectOffset()
    {
        // Real UTC instant is 00:30 (before the 01:00 split → true offset 1h). The naive local-as-UTC
        // probe (02:30) lands AFTER the split and would pick the wrong 2h offset on a single pass.
        var tz = new SteppedTz(new DateTime(2024, 3, 31, 1, 0, 0, DateTimeKind.Utc),
            before: TimeSpan.FromHours(1), atOrAfter: TimeSpan.FromHours(2));
        var local = new DateTime(2024, 3, 31, 2, 30, 0, DateTimeKind.Unspecified);

        var result = await PhotoImportOrchestrator.RefineGpsOffsetAsync(tz, 47.0, 8.0, local, "u1");

        Assert.Equal(TimeSpan.FromHours(1), result); // refined probe (local-2h=00:30) is before the split
    }

    [Fact]
    public async Task RefineGpsOffsetAsync_AwayFromBoundary_SameAsNaiveGuess()
    {
        var tz = new SteppedTz(new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc), before: TimeSpan.FromHours(1), atOrAfter: TimeSpan.FromHours(1));
        var result = await PhotoImportOrchestrator.RefineGpsOffsetAsync(tz, 47.0, 8.0, new DateTime(2024, 6, 1, 12, 0, 0, DateTimeKind.Unspecified), "u1");
        Assert.Equal(TimeSpan.FromHours(1), result);
    }

    [Fact]
    public async Task RefineGpsOffsetAsync_LookupFails_ReturnsNull()
    {
        var result = await PhotoImportOrchestrator.RefineGpsOffsetAsync(new NullTz(), 47.0, 8.0, DateTime.UtcNow, "u1");
        Assert.Null(result);
    }

    // ── minimal stubs ────────────────────────────────────────────────────────
    private sealed class OwnedByMeVisibility(string userId) : IVisibilityService
    {
        public string GetCurrentUserId() => userId;
        public IQueryable<Activity> OwnedActivities(IQueryable<Activity> q) => q.Where(a => a.UserId == userId);
        public Task<List<string>> GetVisibleUserIdsAsync() => throw new NotImplementedException();
        public Task<bool> CanViewItemAsync(string ownerUserId, string? visibility) => throw new NotImplementedException();
        public Task<bool> CanViewActivityAsync(TrackId trackId) => throw new NotImplementedException();
        public IQueryable<Activity> VisibleActivities(IQueryable<Activity> q) => throw new NotImplementedException();
        public IQueryable<Segment> VisibleSegments(IQueryable<Segment> q) => throw new NotImplementedException();
        public IQueryable<MultiDayTour> VisibleTours(IQueryable<MultiDayTour> q) => throw new NotImplementedException();
        public IQueryable<Segment> OwnedSegments(IQueryable<Segment> q) => throw new NotImplementedException();
        public IQueryable<MultiDayTour> OwnedTours(IQueryable<MultiDayTour> q) => throw new NotImplementedException();
        public IQueryable<AppCredentials> OwnedCredentials(IQueryable<AppCredentials> q) => throw new NotImplementedException();
    }

    private sealed class NullTrackReader : ITrackReader
    {
        public Task<DecodedTrack?> GetTrackAsync(TrackId trackId, CancellationToken ct = default) =>
            Task.FromResult<DecodedTrack?>(null);
    }

    // No bbox rows in these fixtures → the point-in-bbox pre-filter yields nothing (NullTrackReader would
    // drop the candidates anyway); every other query is unused by the orchestrator.
    private sealed class NullActivityQuery : IActivityQueryService
    {
        public Task<List<int>> GetTrackIdsContainingPointAsync(double lat, double lng, CancellationToken ct = default) =>
            Task.FromResult(new List<int>());
        public Task<List<Activity>> GetAllActivitiesAsync() => throw new NotImplementedException();
        public Task<Activity?> GetActivityMetadataByIdAsync(int id) => throw new NotImplementedException();
        public Task<Activity?> GetOwnedActivityMetadataByIdAsync(int id) => throw new NotImplementedException();
        public Task<bool> IsActivityOwnedAsync(TrackId trackId) => throw new NotImplementedException();
        public Task<bool> IsActivityVisibleAsync(TrackId trackId) => throw new NotImplementedException();
        public Task<Dictionary<int, ActivityMapSlice>> GetActivitySlicesByIdsAsync(List<int> trackIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<ActivityEndpoint>> GetActivityEndpointsAsync(List<int> trackIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<string>> GetActivityTypesAsync() => throw new NotImplementedException();
        public Task<int> GetVisibleActivityCountAsync() => throw new NotImplementedException();
        public Task<Dictionary<TrackId, int>> GetPhotoCountsAsync(IReadOnlyCollection<TrackId> trackIds, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<(List<Activity> Items, int TotalCount)> GetPagedActivitiesAsync(
            string? activityType, DateTime? startDate, DateTime? endDate, string sortColumn, string sortDirection,
            int page, int pageSize, string? ownerUserId = null, IReadOnlyList<string>? activityTypes = null,
            GridFilterCollection? filters = null, IReadOnlyList<string>? ownerUserIds = null, bool ownerMatchNone = false, IReadOnlyCollection<TrackId>? restrictIds = null, int? knownCount = null)
            => throw new NotImplementedException();
        public Task<List<TrackId>> GetFilteredActivityIdsAsync(
            string? activityType, DateTime? startDate, DateTime? endDate, string? ownerUserId = null,
            IReadOnlyList<string>? activityTypes = null, GridFilterCollection? filters = null,
            IReadOnlyList<string>? ownerUserIds = null, bool ownerMatchNone = false, IReadOnlyCollection<TrackId>? restrictIds = null)
            => throw new NotImplementedException();
        public Task<List<Activity>> GetActivitiesWithGpsCoordinatesAsync(int limit = 100) => throw new NotImplementedException();
        public Task<ActivityStatistics> GetStatisticsAsync(
            string? activityType = null, DateTime? startDate = null, DateTime? endDate = null, string? ownerUserId = null)
            => throw new NotImplementedException();
    }

    private sealed class NullTz : IActivityTimezoneResolver
    {
        public Task<TimeSpan?> ResolveOffsetAsync(double? lat, double? lng, DateTime startUtc, string userId) =>
            Task.FromResult<TimeSpan?>(null);
    }

    // Returns a different offset either side of a fixed UTC split instant — models a DST transition so
    // the two-pass refinement can be exercised deterministically without a real timezone database.
    private sealed class SteppedTz : IActivityTimezoneResolver
    {
        private readonly DateTime _splitUtc;
        private readonly TimeSpan _before, _atOrAfter;
        public SteppedTz(DateTime splitUtc, TimeSpan before, TimeSpan atOrAfter)
        { _splitUtc = splitUtc; _before = before; _atOrAfter = atOrAfter; }
        public Task<TimeSpan?> ResolveOffsetAsync(double? lat, double? lng, DateTime startUtc, string userId) =>
            Task.FromResult<TimeSpan?>(startUtc < _splitUtc ? _before : _atOrAfter);
    }
}
