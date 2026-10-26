using RouteBaseWeb.Models;
using RouteBaseWeb.Models.GridFilter;
using RouteBaseWeb.Models.Sharing;
using RouteBaseWeb.Services.Activities;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Lifecycle;
using RouteBaseWeb.Services.Tracks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Activities;

/// <summary>
/// Locks the behaviour of the view builder extracted out of <c>DetailsModel.OnGetAsync</c>.
///
/// The share-shaped cases are constructed with a NULL EditSessionService on purpose: it is the owner-only
/// dependency, so passing null turns "a share render never loads owner-only data" from a claim into an
/// enforced property — if that path is ever taken, the test throws rather than quietly leaking work.
/// </summary>
public class ActivityDetailViewBuilderTests
{
    private const string Owner = "owner-user";

    private static Activity SampleActivity() => new()
    {
        TrackId = new TrackId(42),
        UserId = Owner,
        TrackName = "Morning ride",
        ActivityType = "Ride",
        HeartRateAvg = 140,
    };

    private static List<GpsData> SampleTrack() =>
    [
        new() { GpsId = 1, GpsLat = 50.0, GpsLong = 4.0, Altitude = 10, Speed = 5, Distance = 0,
                DateTimeUtc = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc), HeartRate = 130 },
        new() { GpsId = 2, GpsLat = 50.001, GpsLong = 4.001, Altitude = 12, Speed = 6, Distance = 500,
                DateTimeUtc = new DateTime(2026, 1, 1, 10, 1, 0, DateTimeKind.Utc), HeartRate = 145 },
    ];

    private static ActivityDetailViewBuilder Build(
        Activity? activity, List<GpsData>? track = null, string? apiKey = "maptiler-secret",
        Activity? ownedOnly = null, List<GpsData>? ownedTrack = null)
        => new(
            new FakeQuery(activity, ownedOnly),
            new FakeGps(track ?? SampleTrack(), ownedTrack),
            new FakeMapTiler(apiKey),
            new FakeSettings(),
            editSessions: null!,          // see class remarks
            new FakeVersionHistory(),
            NullLogger<ActivityDetailViewBuilder>.Instance);

    private static ActivityViewOptions ShareShaped() => new(
        IncludeOwnerData: false,
        IncludeMapTilerKey: false,
        SettingsUserId: null,
        Surfaces: _ => ActivityViewSurfaces.ReadOnlyShare);

    private static ActivityViewOptions PageShaped(string? userId) => new(
        IncludeOwnerData: false,          // owner extras exercised separately; null EditSessionService here
        IncludeMapTilerKey: true,
        SettingsUserId: userId,
        Surfaces: ActivityViewSurfaces.ForDetailsPage);

    // ---- not found ----

    [Fact]
    public async Task Build_ReturnsNull_WhenTheActivityIsNotVisible()
    {
        // The query service already applies visibility, so "not visible" arrives here as null — and must
        // become a 404 rather than a partially-built page.
        var view = await Build(activity: null).BuildAsync(42, ShareShaped());

        Assert.Null(view);
    }

    // ---- the share shape ----

    [Fact]
    public async Task ShareShaped_IsNeverTheOwner_EvenForTheOwnersOwnActivity()
    {
        // A null SettingsUserId cannot match any UserId, so an owner previewing their own link sees exactly
        // what a stranger sees.
        var view = await Build(SampleActivity()).BuildAsync(42, ShareShaped());

        Assert.False(view!.IsOwner);
    }

    [Fact]
    public async Task ShareShaped_OmitsTheMapTilerKey()
    {
        // The key is serialized into the page; a share URL would publish it to anyone holding the link.
        var view = await Build(SampleActivity()).BuildAsync(42, ShareShaped());

        Assert.Equal(string.Empty, view!.MapTilerApiKey);
    }

    [Fact]
    public async Task ShareShaped_OffersNoAffordances()
    {
        var view = await Build(SampleActivity()).BuildAsync(42, ShareShaped());

        var surfaces = view!.Surfaces;
        Assert.False(surfaces.CanEdit);
        Assert.False(surfaces.CanCreateSegments);
        Assert.False(surfaces.CanAddPhotos);
        Assert.False(surfaces.ShowAppNav);
    }

    [Fact]
    public async Task ShareShaped_StillLoadsTheTrackChartsAndSplits()
    {
        // The point of the feature: a share view is the real page, not a stub.
        var view = await Build(SampleActivity()).BuildAsync(42, ShareShaped());

        Assert.Equal(2, view!.GpsData.Count);
        Assert.NotEmpty(view.Splits);
        Assert.True(view.HasHeartRateData);
    }

    // ---- the canonical page shape ----

    [Fact]
    public async Task PageShaped_IsOwner_WhenTheUserIdMatches()
    {
        var view = await Build(SampleActivity()).BuildAsync(42, PageShaped(Owner));

        Assert.True(view!.IsOwner);
        Assert.True(view.Surfaces.CanEdit);
        Assert.True(view.Surfaces.ShowAppNav);
    }

    [Fact]
    public async Task PageShaped_IsNotOwner_ForSomeoneElsesActivity()
    {
        var view = await Build(SampleActivity()).BuildAsync(42, PageShaped("someone-else"));

        Assert.False(view!.IsOwner);
        Assert.False(view.Surfaces.CanEdit);
        // Preserved from before the extraction: a signed-in non-owner may still create a segment from a
        // friend's ride.
        Assert.True(view.Surfaces.CanCreateSegments);
    }

    [Fact]
    public async Task PageShaped_IncludesTheMapTilerKey()
    {
        var view = await Build(SampleActivity()).BuildAsync(42, PageShaped(Owner));

        Assert.Equal("maptiler-secret", view!.MapTilerApiKey);
    }

    [Fact]
    public async Task NullOwnerActivity_IsOwnedByNobody()
    {
        // Legacy null-owner rows are view-only for everyone — a null-owner UI would lie about editability.
        var orphan = SampleActivity();
        orphan.UserId = null;

        var view = await Build(orphan).BuildAsync(42, PageShaped(Owner));

        Assert.False(view!.IsOwner);
    }

    // ---- review mode: the owner's own non-released ride ----

    private static ActivityViewOptions ReviewShaped(string? userId) => new(
        IncludeOwnerData: false,
        IncludeMapTilerKey: true,
        SettingsUserId: userId,
        Surfaces: ActivityViewSurfaces.ForDetailsPage,
        AllowOwnerNonReleased: true);

    private static Activity PendingActivity()
    {
        var a = SampleActivity();
        a.Source = ActivitySources.AutoRecording;
        a.Lifecycle = LifecycleStates.InWork;
        a.LifecycleReason = LifecycleReasons.AwaitingReview;
        return a;
    }

    [Fact]
    public async Task ReviewShaped_OpensTheOwnersPendingRide_ThroughTheOwnerScopedReads()
    {
        // The visible lookup refuses a non-released ride (null), so both the metadata and the track must come
        // from the owner-scoped pair — FakeGps throws if the visible read is used for the points instead.
        var view = await Build(activity: null, ownedOnly: PendingActivity(), ownedTrack: SampleTrack())
            .BuildAsync(42, ReviewShaped(Owner));

        Assert.NotNull(view);
        Assert.Equal(2, view!.GpsData.Count);
        Assert.True(view.IsOwner);
    }

    [Fact]
    public async Task ReviewShaped_OffersNoEditorSegmentsOrPhotos()
    {
        // A ride that is not in circulation yet: the review banner's verbs are the only ones that apply.
        var view = await Build(activity: null, ownedOnly: PendingActivity(), ownedTrack: SampleTrack())
            .BuildAsync(42, ReviewShaped(Owner));

        var surfaces = view!.Surfaces;
        Assert.False(surfaces.CanEdit);
        Assert.False(surfaces.CanCreateSegments);
        Assert.False(surfaces.CanAddPhotos);
        Assert.True(surfaces.ShowAppNav); // it is still the app, not a share view
    }

    [Fact]
    public async Task ReviewShaped_LeavesAReleasedRideOnTheNormalPath()
    {
        // The flag is a fallback, not a mode: a visible ride never consults the owner-scoped reads (FakeGps
        // would throw) and keeps the full details-page surfaces.
        var view = await Build(SampleActivity()).BuildAsync(42, ReviewShaped(Owner));

        Assert.True(view!.Surfaces.CanEdit);
        Assert.True(view.Surfaces.CanCreateSegments);
    }

    [Fact]
    public async Task WithoutTheFlag_ANonReleasedRideIsStillNotFound()
    {
        // Every other caller keeps the released-only behaviour, even though the owner-scoped row exists.
        var view = await Build(activity: null, ownedOnly: PendingActivity(), ownedTrack: SampleTrack())
            .BuildAsync(42, PageShaped(Owner));

        Assert.Null(view);
    }

    [Fact]
    public async Task ShareShaped_NeverReachesForTheOwnerScopedRow()
    {
        // The share path must not widen: it carries no ambient identity by design, so an owner opening their
        // own link keeps seeing exactly what a stranger sees — including the 404 on a non-released ride.
        var view = await Build(activity: null, ownedOnly: PendingActivity(), ownedTrack: SampleTrack())
            .BuildAsync(42, ShareShaped());

        Assert.Null(view);
    }

    // ---- degenerate track ----

    [Fact]
    public async Task EmptyTrack_StillBuildsAPage_WithNoSplits()
    {
        // Matches the pre-extraction behaviour: a GPS-less activity renders rather than 404s.
        var view = await Build(SampleActivity(), track: []).BuildAsync(42, ShareShaped());

        Assert.NotNull(view);
        Assert.Empty(view!.GpsData);
        Assert.Empty(view.Splits);
    }

    [Fact]
    public async Task ColumnarTrack_CarriesEveryAlwaysPresentChannelOncePerPoint()
    {
        var view = await Build(SampleActivity()).BuildAsync(42, ShareShaped());

        var json = System.Text.Json.JsonSerializer.Serialize(view!.GetGpsTrackForJavaScript());
        var parsed = System.Text.Json.JsonDocument.Parse(json).RootElement;

        // The channels every render needs. gpsId/heartRate/cadence are conditional now — see the test below.
        foreach (var channel in new[] { "lat", "lng", "altitude", "speed", "distance", "t" })
            Assert.Equal(2, parsed.GetProperty(channel).GetArrayLength());

        // Speed is m/s in storage and km/h on the wire; distance metres → km.
        Assert.Equal(18.0, parsed.GetProperty("speed")[0].GetDouble(), 3);
        Assert.Equal(0.5, parsed.GetProperty("distance")[1].GetDouble(), 3);
    }

    /// <summary>
    /// Replaces the "all nine channels, always" half of the test above. That pinned the OLD contract; the
    /// wire format deliberately changed to omit channels nothing on the page can read, because a column of
    /// zeros (or of ids only the segment panel uses) is pure payload. Omission — not null — is what saves
    /// the bytes, and the TS side reads these with <c>?.[i]</c>.
    ///
    /// SampleTrack has heart rate and no cadence, so one conditional channel of each kind is exercised.
    /// </summary>
    [Fact]
    public async Task ColumnarTrack_OmitsChannelsThisRenderCannotUse()
    {
        var view = await Build(SampleActivity()).BuildAsync(42, ShareShaped());

        var json = System.Text.Json.JsonSerializer.Serialize(view!.GetGpsTrackForJavaScript());
        var parsed = System.Text.Json.JsonDocument.Parse(json).RootElement;

        // Share view: the segment panel cannot open, so its gpsId lookup table is dead weight.
        Assert.False(parsed.TryGetProperty("gpsId", out _));
        // No cadence sensor on this ride.
        Assert.False(parsed.TryGetProperty("cadence", out _));
        // Heart rate WAS recorded, so it stays — the two are judged independently, not as a pair.
        Assert.Equal(2, parsed.GetProperty("heartRate").GetArrayLength());
    }

    /// <summary>The same track on the owner's own page keeps gpsId — the panel can open there.</summary>
    [Fact]
    public async Task ColumnarTrack_KeepsGpsIdWhereTheSegmentPanelCanOpen()
    {
        var view = await Build(SampleActivity()).BuildAsync(42, PageShaped(Owner));

        var json = System.Text.Json.JsonSerializer.Serialize(view!.GetGpsTrackForJavaScript());
        var parsed = System.Text.Json.JsonDocument.Parse(json).RootElement;

        Assert.Equal(2, parsed.GetProperty("gpsId").GetArrayLength());
    }

    // ---- fakes ----

    /// <summary><paramref name="ownedOnly"/> is what the OWNER-scoped lookup returns — the non-released ride
    /// the visible lookup refuses. Null means the builder must never reach for it.</summary>
    private sealed class FakeQuery(Activity? activity, Activity? ownedOnly = null) : IActivityQueryService
    {
        public Task<Activity?> GetActivityMetadataByIdAsync(int id) => Task.FromResult(activity);
        public Task<Activity?> GetOwnedActivityMetadataByIdAsync(int id) => Task.FromResult(ownedOnly);

        public Task<List<Activity>> GetAllActivitiesAsync() => throw new NotSupportedException();
        public Task<bool> IsActivityOwnedAsync(TrackId trackId) => throw new NotSupportedException();
        public Task<bool> IsActivityVisibleAsync(TrackId trackId) => throw new NotSupportedException();
        public Task<Dictionary<int, ActivityMapSlice>> GetActivitySlicesByIdsAsync(
            List<int> trackIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<ActivityEndpoint>> GetActivityEndpointsAsync(
            List<int> trackIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<string>> GetActivityTypesAsync() => throw new NotSupportedException();
        public Task<List<int>> GetTrackIdsContainingPointAsync(
            double lat, double lng, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> GetVisibleActivityCountAsync() => throw new NotSupportedException();
        public Task<Dictionary<TrackId, int>> GetPhotoCountsAsync(
            IReadOnlyCollection<TrackId> trackIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<(List<Activity> Items, int TotalCount)> GetPagedActivitiesAsync(
            string? activityType, DateTime? startDate, DateTime? endDate, string sortColumn,
            string sortDirection, int page, int pageSize, string? ownerUserId = null,
            IReadOnlyList<string>? activityTypes = null, GridFilterCollection? filters = null,
            IReadOnlyList<string>? ownerUserIds = null, bool ownerMatchNone = false,
            IReadOnlyCollection<TrackId>? restrictIds = null, int? knownCount = null) => throw new NotSupportedException();
        public Task<List<TrackId>> GetFilteredActivityIdsAsync(
            string? activityType, DateTime? startDate, DateTime? endDate, string? ownerUserId = null,
            IReadOnlyList<string>? activityTypes = null, GridFilterCollection? filters = null,
            IReadOnlyList<string>? ownerUserIds = null, bool ownerMatchNone = false,
            IReadOnlyCollection<TrackId>? restrictIds = null) => throw new NotSupportedException();
        public Task<List<Activity>> GetActivitiesWithGpsCoordinatesAsync(int limit = 100)
            => throw new NotSupportedException();
        public Task<ActivityStatistics> GetStatisticsAsync(
            string? activityType = null, DateTime? startDate = null, DateTime? endDate = null,
            string? ownerUserId = null) => throw new NotSupportedException();
    }

    /// <summary>The owner-scoped read throws unless a test supplies <paramref name="ownedTrack"/>: a review
    /// render that fell back to the visible read would then produce the wrong track, loudly.</summary>
    private sealed class FakeGps(List<GpsData> track, List<GpsData>? ownedTrack = null) : IActivityGpsService
    {
        public Task<List<GpsData>> GetGpsDataForTrackAsync(TrackId trackId, CancellationToken ct = default)
            => Task.FromResult(track);

        public Task<List<GpsData>> GetOwnedGpsDataForTrackAsync(TrackId trackId, CancellationToken ct = default)
            => ownedTrack is null ? throw new NotSupportedException() : Task.FromResult(ownedTrack);

        public Task<List<GpsCoordinate>> GetTrackCoordinatesAsync(TrackId trackId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<List<GpsCoordinate>> GetTrackCoordinatesAsync(
            TrackId trackId, int maxPoints, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<List<GpsCoordinate>> GetOwnedTrackCoordinatesAsync(
            TrackId trackId, int maxPoints, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Dictionary<int, List<GpsCoordinate>>> GetBulkTrackCoordinatesAsync(
            List<int> trackIds, int maxPointsPerTrack, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class FakeMapTiler(string? key) : IMapTilerService
    {
        public Task<string?> GetApiKeyAsync() => Task.FromResult(key);
        public Task<bool> SaveApiKeyAsync(string apiKey) => throw new NotSupportedException();
        public Task<bool> HasApiKeyAsync() => throw new NotSupportedException();
    }

    /// <summary>Returns the caller's default for everything — the real service's behaviour for a user with
    /// no saved settings, which is exactly what a share render (null userId) sees.</summary>
    private sealed class FakeSettings : IProfileSettingsService
    {
        public Task<T?> GetSettingAsync<T>(
            string category, string key, string? userId = null, T? defaultValue = default)
            => Task.FromResult(defaultValue);
        public Task<string?> GetSettingAsync(string category, string key, string? userId = null)
            => Task.FromResult<string?>(null);
        public Task<bool> SetSettingAsync(
            string category, string key, object value, string userId)
            => throw new NotSupportedException();
        public Task SetSettingsAsync(
            IReadOnlyList<(string Category, string Key, object Value)> settings, string userId)
            => throw new NotSupportedException();
        // Reads only — no fake here stores anything a delete could remove.
        public Task<bool> DeleteSettingAsync(string category, string key, string userId) =>
            Task.FromResult(false);

        public Task<Dictionary<string, string>> GetCategorySettingsAsync(
            string category, string? userId = null)
            => Task.FromResult(new Dictionary<string, string>(StringComparer.Ordinal));
    }

    private sealed class FakeVersionHistory : ITrackVersionHistory
    {
        public Task<bool> OwnsAsync(TrackId trackId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<TrackHistoryFacts> FactsAsync(TrackId trackId, CancellationToken ct = default)
            => Task.FromResult(new TrackHistoryFacts(0, false));
        public Task<IReadOnlyList<TrackVersionView>> ListAsync(TrackId trackId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<string?> ResetToOriginalAsync(TrackId trackId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<string?> RestoreAsync(TrackId trackId, VersionId versionId, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}
