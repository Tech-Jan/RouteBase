using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using RouteBaseWeb.Services.Races;
using RouteBaseWeb.Services.Tracks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Races;

/// <summary>
/// The course grid is cached per CONTENT, not per track id.
///
/// Keyed by id alone it went stale across a version switch, and nothing short of a process restart could
/// clear it: refreshing a course's elevation moves the head to a new object, and the replay kept serving
/// the grid built from the old one — so reverting the refresh did not bring the race back either.
/// </summary>
public class RaceCourseServiceCacheTests : IAsyncLifetime
{
    private const int TrackId = 7;

    private RouteBaseDbContext _context = null!;
    private TrackContentStore _content = null!;
    private FakeTrackReader _reader = null!;
    private RaceCourseService _service = null!;

    public async Task InitializeAsync()
    {
        var conn = $"Data Source=file:racecourse_test_{Guid.NewGuid():N}?mode=memory&cache=shared";
        var options = new DbContextOptionsBuilder<RouteBaseDbContext>().UseSqlite(conn).Options;
        _context = new RouteBaseDbContext(options);
        await _context.Database.OpenConnectionAsync();
        await _context.Database.EnsureCreatedAsync();
        await new DbSchemaManager(_context).EnsureTrackContentTablesAsync();

        _context.Users.Add(new AppUser { UserId = "u", DisplayName = "u" });
        _context.Activities.Add(new Activity { TrackId = new TrackId(TrackId), UserId = "u" });
        await _context.SaveChangesAsync();

        _content = new TrackContentStore(_context, new TrackObjectStore(_context, TimeProvider.System), TimeProvider.System);
        _reader = new FakeTrackReader();
        _service = new RaceCourseService(
            _reader, _content, new MemoryCache(new MemoryCacheOptions()),
            NullLogger<RaceCourseService>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _context.Database.CloseConnectionAsync();
        await _context.DisposeAsync();
    }

    /// <summary>
    /// A flat 3 km course, every point at <paramref name="altitude"/> (NaN = no DEM data).
    /// <paramref name="noFix"/> marks points the receiver never fixed — NULL coordinates, read back as NaN.
    /// </summary>
    private static DecodedTrack FlatTrack(double altitude, Func<int, bool>? noFix = null)
    {
        const int n = 300;
        var time = new double[n];
        var lat = new double[n];
        var lng = new double[n];
        var alt = new double[n];
        for (var i = 0; i < n; i++)
        {
            time[i] = i;
            lat[i] = noFix?.Invoke(i) == true ? double.NaN : 47.9;
            lng[i] = noFix?.Invoke(i) == true
                ? double.NaN
                : 8.1 + i * 10.0 / (111_320.0 * Math.Cos(47.9 * Math.PI / 180.0));
            alt[i] = altitude;
        }
        return DecodedTrackExtensions.FromDoubleChannels(
            time, lat, lng, alt, null, null, null, null, null, null);
    }

    /// <summary>Move the head to a NEW object — what an elevation refresh (or its revert) does.</summary>
    private async Task PublishVersionAsync(DecodedTrack track)
    {
        _reader.Track = track;
        await _content.AppendVersionAsync(new TrackId(TrackId), TrackBlobCodec.Encode(track), track.Count);
    }

    [Fact]
    public async Task GridIsRebuilt_WhenTheHeadMovesToANewVersion()
    {
        await PublishVersionAsync(FlatTrack(100.0));
        var before = await _service.GetCourseGridAsync(new TrackId(TrackId));

        await PublishVersionAsync(FlatTrack(500.0));
        var after = await _service.GetCourseGridAsync(new TrackId(TrackId));

        Assert.NotNull(before);
        Assert.NotNull(after);
        Assert.Equal(100.0, before!.Elevation[0], 1);
        Assert.Equal(500.0, after!.Elevation[0], 1);   // the stale grid would still read 100
    }

    [Fact]
    public async Task GridIsServedFromCache_WhileTheHeadStandsStill()
    {
        await PublishVersionAsync(FlatTrack(100.0));

        var first = await _service.GetCourseGridAsync(new TrackId(TrackId));
        var second = await _service.GetCourseGridAsync(new TrackId(TrackId));

        Assert.Same(first, second);
        Assert.Equal(1, _reader.Reads);   // the second call never re-decoded the track
    }

    /// <summary>The bug in the wild: a refresh that found no DEM data (water) poisons nothing.</summary>
    [Fact]
    public async Task AnAltitudeChannelOfNaN_YieldsAFiniteProfile()
    {
        await PublishVersionAsync(FlatTrack(double.NaN));

        var grid = await _service.GetCourseGridAsync(new TrackId(TrackId));

        Assert.NotNull(grid);
        Assert.All(grid!.Elevation, e => Assert.True(double.IsFinite(e)));
        Assert.All(grid.GradePercent, g => Assert.True(double.IsFinite(g)));
    }

    /// <summary>A point with no fix is not a place: one NaN coordinate poisons the arc length of every
    /// point after it, and with it every node of the grid.</summary>
    [Fact]
    public async Task PointsWithNoGpsFix_AreDroppedFromTheGrid()
    {
        await PublishVersionAsync(FlatTrack(100.0, noFix: i => i % 10 == 0));

        var grid = await _service.GetCourseGridAsync(new TrackId(TrackId));

        Assert.NotNull(grid);
        Assert.All(grid!.Lat, v => Assert.True(double.IsFinite(v)));
        Assert.All(grid.Lng, v => Assert.True(double.IsFinite(v)));
        Assert.All(grid.Elevation, v => Assert.True(double.IsFinite(v)));
    }

    /// <summary>The grid carries the sha it was built from, so everything DERIVED from a grid — the cost
    /// curve is — can key on it and be refitted when a recalculation moves the course.</summary>
    [Fact]
    public async Task TheGridCarriesTheContentShaItWasBuiltFrom()
    {
        await PublishVersionAsync(FlatTrack(100.0));
        var before = await _service.GetCourseGridAsync(new TrackId(TrackId));

        await PublishVersionAsync(FlatTrack(500.0));
        var after = await _service.GetCourseGridAsync(new TrackId(TrackId));

        Assert.NotNull(before!.SourceSha);
        Assert.NotEqual(before.SourceSha, after!.SourceSha, StringComparer.Ordinal);
    }

    private sealed class FakeTrackReader : ITrackReader
    {
        public DecodedTrack? Track { get; set; }
        public int Reads { get; private set; }

        public Task<DecodedTrack?> GetTrackAsync(TrackId trackId, CancellationToken ct = default)
        {
            Reads++;
            return Task.FromResult(Track);
        }
    }
}
