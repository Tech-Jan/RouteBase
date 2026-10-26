using System.Reflection;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Geometry;
using RouteBaseWeb.Models.Sharing;
using RouteBaseWeb.Services.Heatmaps;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Segments;
using RouteBaseWeb.Services.Segments.Rules;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Segments;

/// <summary>
/// Locks the behaviour of the view builder extracted out of <c>StatisticsModel.OnGetAsync</c>.
///
/// The share-shaped cases are constructed with a NULL IHeatmapLifecycle on purpose: it is the dependency a
/// share render must never touch, so passing null turns "a share render never loads heatmap data" from a
/// claim into an enforced property — if that path is ever taken, the test throws rather than quietly
/// re-enabling the heatmap poll.
/// </summary>
public class SegmentDetailViewBuilderTests
{
    private const string Owner = "owner-user";

    private static Segment SampleSegment(string? userId = Owner) => new()
    {
        SegmentId = new SegmentId(7),
        SegmentName = "Test climb",
        UserId = userId,
        SegmentKind = "polyline",
        SegmentDistance = 2500,
    };

    private static List<SegmentEffortDetailed> SampleEfforts() =>
    [
        new() { GpsInSegmentId = 1, SegmentId = 7, ActivityId = 100, ElapsedTime = 720, Distance = 2500 },
        new() { GpsInSegmentId = 2, SegmentId = 7, ActivityId = 101, ElapsedTime = 751, Distance = 2500 },
    ];

    private static SegmentEffortTotals SampleTotals() =>
        new() { EffortCount = 2, TotalDistance = 5000, TotalElevation = 200, TotalTime = 1471 };

    /// <param name="heatmaps">null on share-shaped builds — see class remarks.</param>
    private static SegmentDetailViewBuilder Build(string? apiKey = "maptiler-secret",
                                                  IHeatmapLifecycle? heatmaps = null)
        => new(new FakeMapTiler(apiKey), new FakeSettings(), heatmaps!);

    private static SegmentViewOptions ShareShaped() => new(
        IncludeMapTilerKey: false,
        IncludeHeatmap: false,
        SettingsUserId: null,
        Surfaces: _ => SegmentViewSurfaces.ReadOnlyShare,
        ApiBase: "/s/tok/api/segment");

    private static SegmentViewOptions PageShaped(string? userId) => new(
        IncludeMapTilerKey: true,
        IncludeHeatmap: true,
        SettingsUserId: userId,
        Surfaces: SegmentViewSurfaces.ForStatisticsPage);

    // ---- structural: no ambient identity ----

    [Fact]
    public void Builder_TakesNoCurrentUserService()
    {
        // GetUserId() THROWS on an anonymous principal, so a builder able to reach for it would be one
        // refactor away from breaking every share render. Enforced at the constructor, not by discipline.
        var ctor = typeof(SegmentDetailViewBuilder).GetConstructors(
            BindingFlags.Public | BindingFlags.Instance).Single();

        Assert.NotEmpty(ctor.GetParameters()); // anti-vacuity
        Assert.DoesNotContain(ctor.GetParameters(),
            p => typeof(ICurrentUserService).IsAssignableFrom(p.ParameterType));
    }

    // ---- not found ----

    [Fact]
    public async Task Build_ReturnsNull_WhenTheSegmentIsNotVisible()
    {
        // The source already applies the gate, so "not visible" arrives here as null — and must become a
        // 404 rather than a partially-built page.
        var view = await Build().BuildAsync(new SegmentId(7), new FakeSource(segment: null), ShareShaped());

        Assert.Null(view);
    }

    // ---- the share shape ----

    [Fact]
    public async Task ShareShaped_IsNeverTheOwner_EvenForTheOwnersOwnSegment()
    {
        // A null SettingsUserId cannot match any UserId, so an owner previewing their own link sees exactly
        // what a stranger sees.
        var view = await Build().BuildAsync(new SegmentId(7), new FakeSource(SampleSegment()), ShareShaped());

        Assert.False(view!.IsOwner);
    }

    [Fact]
    public async Task ShareShaped_OmitsTheMapTilerKey()
    {
        var view = await Build().BuildAsync(new SegmentId(7), new FakeSource(SampleSegment()), ShareShaped());

        Assert.Equal(string.Empty, view!.MapTilerApiKey);
    }

    [Fact]
    public async Task ShareShaped_NeverTouchesTheHeatmapLifecycle()
    {
        // Built with a null lifecycle: reaching it at all would NullReference here. This is the load-bearing
        // half of heatmap suppression — no Heatmap on the view means the config island omits
        // hasHeatmap/heatmapStatus, so heatmap-controls computes 'none' and never starts its poll.
        var view = await Build(heatmaps: null).BuildAsync(new SegmentId(7), new FakeSource(SampleSegment()), ShareShaped());

        Assert.Null(view!.Heatmap);
    }

    [Fact]
    public async Task ShareShaped_OffersNoAffordances()
    {
        var view = await Build().BuildAsync(new SegmentId(7), new FakeSource(SampleSegment()), ShareShaped());

        var s = view!.Surfaces;
        Assert.False(s.CanManageSegment);
        Assert.False(s.CanGenerateHeatmap);
        Assert.False(s.ShowHeatmap);
        Assert.False(s.CanExcludeEfforts);
        Assert.False(s.ShowRiderIdentity);
        Assert.False(s.CanLinkToActivities);
        Assert.False(s.CanShare);
        Assert.False(s.ShowAppNav);
    }

    [Fact]
    public async Task ShareShaped_StillLoadsEffortsAndTotals()
    {
        // The point of the feature: a share view is the real page, not a stub.
        var view = await Build().BuildAsync(
            new SegmentId(7), new FakeSource(SampleSegment(), SampleEfforts(), SampleTotals()), ShareShaped());

        Assert.Equal(2, view!.DetailedEfforts.Count);
        Assert.Equal(2, view.EffortTotals!.EffortCount);
    }

    [Fact]
    public async Task ShareShaped_CarriesTheShareApiBase()
    {
        // The token-mounted base is what lets ONE set of frontend modules serve both routes.
        var view = await Build().BuildAsync(new SegmentId(7), new FakeSource(SampleSegment()), ShareShaped());

        Assert.Equal("/s/tok/api/segment", view!.ApiBase);
    }

    // ---- the canonical page shape ----

    [Fact]
    public async Task PageShaped_IsOwner_WhenTheUserIdMatches()
    {
        var view = await Build(heatmaps: new FakeHeatmaps())
            .BuildAsync(new SegmentId(7), new FakeSource(SampleSegment()), PageShaped(Owner));

        Assert.True(view!.IsOwner);
        Assert.True(view.Surfaces.CanManageSegment);
        Assert.True(view.Surfaces.ShowAppNav);
    }

    [Fact]
    public async Task PageShaped_IsNotOwner_ForSomeoneElsesSegment()
    {
        var view = await Build(heatmaps: new FakeHeatmaps())
            .BuildAsync(new SegmentId(7), new FakeSource(SampleSegment()), PageShaped("someone-else"));

        Assert.False(view!.IsOwner);
        Assert.False(view.Surfaces.CanManageSegment);
        // Preserved from before the extraction: a signed-in non-owner still sees rider names, activity
        // links and the heatmap.
        Assert.True(view.Surfaces.ShowRiderIdentity);
        Assert.True(view.Surfaces.CanLinkToActivities);
        Assert.True(view.Surfaces.ShowHeatmap);
    }

    [Fact]
    public async Task PageShaped_LegacyNullOwnerSegment_IsOwnedByNobody()
    {
        // Matches OwnedSegments write semantics — deliberately not Details' IsNullOrEmpty variant (PD-08).
        var view = await Build(heatmaps: new FakeHeatmaps())
            .BuildAsync(new SegmentId(7), new FakeSource(SampleSegment(userId: null)), PageShaped(Owner));

        Assert.False(view!.IsOwner);
        Assert.False(view.Surfaces.CanManageSegment);
    }

    [Fact]
    public async Task PageShaped_DefaultsToTheAuthenticatedApiBase()
    {
        var view = await Build(heatmaps: new FakeHeatmaps())
            .BuildAsync(new SegmentId(7), new FakeSource(SampleSegment()), PageShaped(Owner));

        Assert.Equal("/api/SegmentsApi", view!.ApiBase);
    }

    // ---- the page-shape data that had no assertions ----

    // Mut 65 (conditional-false → key never included) + mut 66 (null-coalesce remove-left → always empty):
    // when the key IS included, the real key reaches the view.
    //
    // The mut-81 half of this case (ShowDebugInfo defaults false) was DROPPED, not weakened: the settings
    // redesign deleted SegmentDetailView.ShowDebugInfo outright. It was a second copy of a display
    // preference that reaches every page as the #appSettings island, and nothing read it — so there is no
    // longer a property here to mutate. That default is pinned where the setting now lives
    // (SettingsEmptyStoreDefaultsTests, BootstrapContractTests).
    [Fact]
    public async Task PageShaped_IncludesTheMapTilerKey()
    {
        var view = await Build(apiKey: "maptiler-secret", heatmaps: new FakeHeatmaps())
            .BuildAsync(new SegmentId(7), new FakeSource(SampleSegment()), PageShaped(Owner));

        Assert.Equal("maptiler-secret", view!.MapTilerApiKey);
    }

    // Mut 66 (`?? string.Empty` → `"Stryker was here!"`): a page render whose MapTiler service has no key
    // falls back to the EMPTY string, not a sentinel — the basemap registry then uses keyless styles.
    [Fact]
    public async Task PageShaped_MissingApiKey_FallsBackToEmptyString()
    {
        var view = await Build(apiKey: null, heatmaps: new FakeHeatmaps())
            .BuildAsync(new SegmentId(7), new FakeSource(SampleSegment()), PageShaped(Owner));

        Assert.Equal(string.Empty, view!.MapTilerApiKey);
    }

    // Mut 87 (`SegmentKind == Shapes` → `!=`) + mut 90-93 (Min/Max corner normalization flipped): a shapes
    // segment surfaces its zone rects with corners normalized to (min,max) regardless of input order.
    [Fact]
    public async Task ShapesSegment_SurfacesZoneRects_WithNormalizedCorners()
    {
        var view = await Build().BuildAsync(
            new SegmentId(7), new FakeSource(ShapeSegment()), ShareShaped());

        var rect = Assert.Single(view!.ShapeRects);
        Assert.Equal(46.999, rect.MinLat,  precision: 6);   // Math.Min(South, North)
        Assert.Equal(47.011, rect.MaxLat,  precision: 6);   // Math.Max(South, North)
        Assert.Equal(10.999, rect.MinLong, precision: 6);   // Math.Min(West, East)
        Assert.Equal(11.001, rect.MaxLong, precision: 6);   // Math.Max(West, East)
        Assert.Equal(ShapeKinds.Zone, rect.Role);
    }

    [Fact]
    public async Task PolylineSegment_HasNoShapeRects()
    {
        var view = await Build().BuildAsync(
            new SegmentId(7), new FakeSource(SampleSegment()), ShareShaped());

        Assert.Empty(view!.ShapeRects);
    }

    // Mut 103 (conditional-false → heatmap never loaded): a page render that includes the heatmap surfaces
    // whatever the lifecycle returns. The share path (IncludeHeatmap:false) is pinned separately above.
    [Fact]
    public async Task PageShaped_LoadsTheHeatmap_WhenIncluded()
    {
        var view = await Build(heatmaps: new FakeHeatmapsWithData())
            .BuildAsync(new SegmentId(7), new FakeSource(SampleSegment()), PageShaped(Owner));

        Assert.NotNull(view!.Heatmap);
        Assert.Equal(5, view.Heatmap!.HeatmapId);
    }

    private static Segment ShapeSegment()
    {
        var rules = new RuleDocument
        {
            Shapes = new()
            {
                new ShapeDef
                {
                    No = 1, Kind = "zone",
                    Geom = new RectGeom { South = 46.999, West = 10.999, North = 47.011, East = 11.001 },
                },
            },
            Extent = new WholeExtent(),
            Membership = { new PassesAnyRule { Zones = { 1 } } },
        };
        return new Segment
        {
            SegmentId = new SegmentId(7),
            SegmentName = "Shape climb",
            UserId = Owner,
            SegmentKind = SegmentKinds.Shapes,
            MatchRulesJson = rules.Serialize(),
        };
    }

    /// <summary>A lifecycle that returns a real heatmap — the inverse of <see cref="FakeHeatmaps"/>, for the
    /// page path where the heatmap must actually be loaded.</summary>
    private sealed class FakeHeatmapsWithData : IHeatmapLifecycle
    {
        public Task<Heatmap?> GetVisibleAsync(SegmentId segmentId, CancellationToken ct = default)
            => Task.FromResult<Heatmap?>(new Heatmap { HeatmapId = 5, SegmentId = segmentId, Status = "done" });
        public Task<HeatmapAttachResult> AttachAsync(SegmentId segmentId, IEnumerable<string> activityTypes)
            => throw new NotSupportedException();
        public Task FailPendingAsync(SegmentId segmentId, string error) => throw new NotSupportedException();
        public Task WithdrawAsync(SegmentId segmentId, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkStaleAsync(SegmentId segmentId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task MarkStaleForTrackAsync(TrackId trackId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<int?> CaptureIdForSegmentAsync(SegmentId segmentId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public void PurgeTiles(int heatmapId) => throw new NotSupportedException();
    }

    // ---- fakes ----

    private sealed class FakeSource(
        Segment? segment,
        List<SegmentEffortDetailed>? efforts = null,
        SegmentEffortTotals? totals = null) : ISegmentViewSource
    {
        public Task<Segment?> GetSegmentAsync(SegmentId segmentId, CancellationToken ct = default)
            => Task.FromResult(segment);
        public Task<List<SegmentEffortDetailed>> GetEffortsAsync(SegmentId segmentId, CancellationToken ct = default)
            => Task.FromResult(efforts ?? []);
        public Task<SegmentEffortTotals?> GetTotalsAsync(SegmentId segmentId, CancellationToken ct = default)
            => Task.FromResult(totals);
    }

    /// <summary>Every member throws except the one read the canonical page makes — so a build that reaches
    /// for anything else fails loudly instead of silently doing owner work.</summary>
    private sealed class FakeHeatmaps : IHeatmapLifecycle
    {
        public Task<Heatmap?> GetVisibleAsync(SegmentId segmentId, CancellationToken ct = default)
            => Task.FromResult<Heatmap?>(null);
        public Task<HeatmapAttachResult> AttachAsync(SegmentId segmentId, IEnumerable<string> activityTypes)
            => throw new NotSupportedException();
        public Task FailPendingAsync(SegmentId segmentId, string error) => throw new NotSupportedException();
        public Task WithdrawAsync(SegmentId segmentId, CancellationToken ct = default) => Task.CompletedTask;
        public Task MarkStaleAsync(SegmentId segmentId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task MarkStaleForTrackAsync(TrackId trackId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<int?> CaptureIdForSegmentAsync(SegmentId segmentId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public void PurgeTiles(int heatmapId) => throw new NotSupportedException();
    }

    // B3: DateIso goes out as day-at-midnight wall-clock ISO — bare yyyy-MM-dd parses as UTC
    // midnight in the browser and shifted a day for UTC-minus viewers.
    [Fact]
    public void EffortRow_DateIso_IsWallClockMidnightIso()
    {
        var e = new SegmentEffortDetailed { ActivityDate = new DateTime(2026, 7, 1, 14, 5, 30, DateTimeKind.Unspecified) };

        Assert.Equal("2026-07-01T00:00:00", SegmentEffortRowDto.From(e, 0, showRiderIdentity: true).DateIso);
        Assert.Null(SegmentEffortRowDto.From(new SegmentEffortDetailed(), 0, showRiderIdentity: true).DateIso);
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
}
