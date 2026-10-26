using RouteBaseWeb.Api;
using RouteBaseWeb.Models;
using RouteBaseWeb.Models.Sharing;
using RouteBaseWeb.Services.Sharing;
using RouteBaseWeb.Services.Tracks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace RouteBaseWeb.Tests.Api;

/// <summary>
/// The anonymous segment surface's INPUT hygiene: caller-supplied ints are clamped before they reach the
/// reader (where <c>maxPoints</c> is a cache-key component in a size-unbounded cache), and coordinates are
/// rounded at the wire boundary like every authenticated GPS endpoint.
/// </summary>
public class ShareSegmentReadControllerTests
{
    private const int SegId = 7;

    private static ShareGrant SegGrant() =>
        new(TokenId: 1, ShareKind.Segment, SegId, "owner", DateTime.UtcNow.AddHours(1));

    private static ShareSegmentReadController Controller(IShareLinkAccess access, IShareSegmentReader reader) =>
        new(access, reader) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };

    [Theory]
    [InlineData(0, 0)]          // 0 = full resolution, preserved — replay depends on it (authenticated twin)
    [InlineData(-5, 0)]         // negatives collapse to the same full-res sentinel, not a distinct cache key
    [InlineData(7, 10)]         // positive values snap into [10, 5000] exactly like SegmentsApiController
    [InlineData(500, 500)]
    [InlineData(999_999, 5000)]
    public async Task Performance_ClampsMaxPoints_BeforeItReachesTheReader(int requested, int expected)
    {
        var reader = new CapturingReader();

        await Controller(new FakeAccess(SegGrant()), reader).Performance("1", requested, CancellationToken.None);

        Assert.Equal(expected, reader.PerfMaxPoints);
    }

    /// <summary>
    /// The point budget is the RESOLUTION of a payload whose endpoints are the owner's home. Unclamped,
    /// <c>maxPoints=99999999</c> drives Downsample's stride to 1 and the ANONYMOUS surface serves full
    /// resolution the authenticated one refuses — a weaker limit on the more exposed route.
    /// </summary>
    [Theory]
    [InlineData(0, ShareSegmentReader.DefaultMaxPointsPerTrack)]   // unset → the default, never full-res
    [InlineData(-5, ShareSegmentReader.DefaultMaxPointsPerTrack)]  // negatives are not a full-res sentinel here
    [InlineData(500, 500)]
    [InlineData(99_999_999, ShareSegmentReadController.BulkMaxPointsPerTrack)]
    public async Task Tracks_ClampsMaxPoints_ToTheAuthenticatedBulkCeiling(int requested, int expected)
    {
        var reader = new CapturingReader();

        await Controller(new FakeAccess(SegGrant()), reader).Tracks(0, 150, requested, CancellationToken.None);

        Assert.Equal(expected, reader.TracksMaxPoints);
    }

    /// <summary>Same rule, the single-track twin — 5000, matching SegmentsApiController.GetFullActivityTrack.</summary>
    [Theory]
    [InlineData(0, ShareSegmentReader.DefaultMaxPointsPerTrack)]
    [InlineData(-5, ShareSegmentReader.DefaultMaxPointsPerTrack)]
    [InlineData(500, 500)]
    [InlineData(99_999_999, ShareSegmentReadController.SingleMaxPointsPerTrack)]
    public async Task SingleTrack_ClampsMaxPoints_ToTheAuthenticatedSingleCeiling(int requested, int expected)
    {
        var reader = new CapturingReader { SingleTrack = [] };

        await Controller(new FakeAccess(SegGrant()), reader).Track(9, requested, CancellationToken.None);

        Assert.Equal(expected, reader.SingleTrackMaxPoints);
    }

    [Fact]
    public async Task Tracks_RoundsCoordinatesToWirePrecision()
    {
        // Full-precision doubles never cross the wire on any authenticated GPS boundary; the anonymous
        // (most bandwidth-sensitive) surface must not be the one exception.
        var reader = new CapturingReader
        {
            TracksResult = new ShareSegmentTracksResult(
                [(9, [new GpsCoordinate { Lat = 47.123456789123, Lng = 8.987654999111, GpsId = 3 }])], 1),
        };

        var result = await Controller(new FakeAccess(SegGrant()), reader).Tracks(0, 150, 0, CancellationToken.None);

        var body = Assert.IsType<ShareSegmentTracksResponse>(Assert.IsType<OkObjectResult>(result).Value);
        var point = Assert.Single(Assert.Single(body.Tracks).Points);
        Assert.Equal(GpsPrecision.Coord(47.123456789123), point.Lat);
        Assert.Equal(GpsPrecision.Coord(8.987654999111), point.Lng);
        Assert.NotEqual(47.123456789123, point.Lat);   // anti-vacuity: the rounding actually changed it
        Assert.Equal(3, point.GpsId);
    }

    [Fact]
    public async Task SingleTrack_RoundsCoordinatesToWirePrecision()
    {
        var reader = new CapturingReader
        {
            SingleTrack = [new GpsCoordinate { Lat = -12.000000455, Lng = 100.999999501, GpsId = 1 }],
        };

        var result = await Controller(new FakeAccess(SegGrant()), reader).Track(9, 0, CancellationToken.None);

        var body = Assert.IsType<ShareSegmentTrackDto>(Assert.IsType<OkObjectResult>(result).Value);
        var point = Assert.Single(body.Points);
        Assert.Equal(GpsPrecision.Coord(-12.000000455), point.Lat);
        Assert.Equal(GpsPrecision.Coord(100.999999501), point.Lng);
    }

    // ---- fakes -------------------------------------------------------------------------------------

    private sealed class FakeAccess(ShareGrant? grant) : IShareLinkAccess
    {
        public Task<ShareGrant?> ResolveAsync(string token, CancellationToken ct) => Task.FromResult(grant);
        public Task<ShareGrant?> CurrentAsync(CancellationToken ct) => Task.FromResult(grant);

        public async Task<T?> WidenAsync<T>(ShareKind kind, int resourceId, Func<Task<T?>> read, CancellationToken ct)
            where T : class
            => grant is null || !grant.Covers(kind, resourceId) ? null : await read();
    }

    private sealed class CapturingReader : IShareSegmentReader
    {
        public int? PerfMaxPoints;
        public int? TracksMaxPoints;
        public int? SingleTrackMaxPoints;
        public ShareSegmentTracksResult TracksResult = new([], 0);
        public List<GpsCoordinate>? SingleTrack;

        public Task<Segment?> GetSegmentAsync(ShareGrant grant, CancellationToken ct = default)
            => Task.FromResult<Segment?>(null);

        public Task<List<SegmentEffortDetailed>> GetOwnerEffortsAsync(ShareGrant grant, CancellationToken ct = default)
            => Task.FromResult(new List<SegmentEffortDetailed>());

        public Task<SegmentEffortTotals?> GetOwnerEffortTotalsAsync(ShareGrant grant, CancellationToken ct = default)
            => Task.FromResult<SegmentEffortTotals?>(null);

        public Task<List<EffortPerformanceData>> GetPerformanceAsync(
            ShareGrant grant, IReadOnlySet<GpsInSegmentId> effortIds, int maxPointsPerEffort, CancellationToken ct = default)
        {
            PerfMaxPoints = maxPointsPerEffort;
            return Task.FromResult(new List<EffortPerformanceData>());
        }

        public Task<ShareSegmentTracksResult> GetTracksAsync(
            ShareGrant grant, int skip, int take, int maxPointsPerTrack, CancellationToken ct = default)
        {
            TracksMaxPoints = maxPointsPerTrack;
            return Task.FromResult(TracksResult);
        }

        public Task<List<GpsCoordinate>?> GetTrackAsync(
            ShareGrant grant, TrackId trackId, int maxPoints, CancellationToken ct = default)
        {
            SingleTrackMaxPoints = maxPoints;
            return Task.FromResult(SingleTrack);
        }
    }
}
