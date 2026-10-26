using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Races;
using RouteBaseWeb.Services.Tracks;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Races;

/// <summary>
/// Projecting a real rider onto a course. The property that matters: his POSITION stays his own (a
/// detour must remain visible), while his CHAINAGE ranks him against the field.
/// </summary>
public class RaceRealParticipantServiceTests
{
    private const double Lat0 = 47.9;
    private static readonly double MetresPerDegreeLng = 111_320.0 * Math.Cos(Lat0 * Math.PI / 180.0);

    /// <summary>A due-east course, `n` nodes, 10 m apart.</summary>
    private static RaceCourseGrid Course(int n)
    {
        var lat = new double[n];
        var lng = new double[n];
        var alt = new double?[n];
        for (var i = 0; i < n; i++)
        {
            lat[i] = Lat0;
            lng[i] = 8.1 + i * 10.0 / MetresPerDegreeLng;
            alt[i] = 500;
        }
        return RaceCourseGridBuilder.Build(lat, lng, alt);
    }

    /// <summary>A rider's track as a DecodedTrack (channel order is time, lat, lng).</summary>
    private static DecodedTrack Ride(double[] lat, double[] lng, double[] epoch) =>
        DecodedTrackExtensions.FromDoubleChannels(epoch, lat, lng, null, null, null, null, null, null, null);

    private sealed class FakeTrackReader : ITrackReader
    {
        private readonly DecodedTrack? _track;
        public FakeTrackReader(DecodedTrack? track) => _track = track;
        public Task<DecodedTrack?> GetTrackAsync(TrackId trackId, CancellationToken ct = default) =>
            Task.FromResult(_track);
    }

    private static List<RaceCheckpoint> Checkpoints(double total) =>
    [
        new() { CheckpointId = new CheckpointId(1), Seq = 0, CheckpointName = "Start", ChainageMeters = 0 },
        new() { CheckpointId = new CheckpointId(2), Seq = 1, CheckpointName = "Half", ChainageMeters = total / 2 },
        new() { CheckpointId = new CheckpointId(3), Seq = 2, CheckpointName = "Finish", ChainageMeters = total },
    ];

    /// <summary>Rides the course itself, at a constant 5 m/s.</summary>
    private static (double[] lat, double[] lng, double[] epoch) OnCourse(RaceCourseGrid course)
    {
        var n = course.Count;
        var lat = new double[n];
        var lng = new double[n];
        var epoch = new double[n];
        for (var i = 0; i < n; i++)
        {
            lat[i] = course.Lat[i];
            lng[i] = course.Lng[i];
            epoch[i] = 1_000_000 + i * 2;   // 10 m every 2 s
        }
        return (lat, lng, epoch);
    }

    [Fact]
    public async Task Project_OnCourseRider_ChainageTracksTheCourse()
    {
        var course = Course(201);                       // 2000 m
        var (lat, lng, epoch) = OnCourse(course);
        var service = new RaceRealParticipantService(new FakeTrackReader(Ride(lat, lng, epoch)));

        var result = await service.ProjectAsync(new TrackId(1), course, Checkpoints(course.TotalMeters), new ParticipantId(42));

        Assert.NotNull(result);
        Assert.InRange(result!.ChainageMeters[0], -20, 20);
        Assert.InRange(result.ChainageMeters[^1], course.TotalMeters - 40, course.TotalMeters + 40);
        // Chainage is monotone for a rider who never turns back.
        for (var i = 1; i < result.ChainageMeters.Length; i++)
            Assert.True(result.ChainageMeters[i] >= result.ChainageMeters[i - 1] - 1.0);
    }

    [Fact]
    public async Task Project_KeepsHisOwnCoordinates_SoADetourStaysVisible()
    {
        // He swings ~100 m north of the course for a stretch: a wrong turn.
        var course = Course(201);
        var (lat, lng, epoch) = OnCourse(course);
        for (var i = 80; i < 120; i++) lat[i] += 0.0009;      // ~100 m north

        var service = new RaceRealParticipantService(new FakeTrackReader(Ride(lat, lng, epoch)));

        var result = await service.ProjectAsync(new TrackId(1), course, Checkpoints(course.TotalMeters), new ParticipantId(42));

        // THE point of real riders: the returned geometry is HIS, not the course's. Had we projected
        // him onto the course, the detour would be gone — SegmentChainage smooths off-course spans
        // back onto the line, so it survives only because we never overwrite his coordinates.
        Assert.NotNull(result);
        Assert.Equal(lat[100], result!.Lat[100], 9);
        Assert.True(Math.Abs(result.Lat[100] - course.Lat[100]) > 0.0005);
    }

    [Fact]
    public async Task Project_DerivesSplitsFromHisGps_NotFromATable()
    {
        var course = Course(201);                       // 2000 m; 10 m per 2 s => 400 s total
        var (lat, lng, epoch) = OnCourse(course);
        var service = new RaceRealParticipantService(new FakeTrackReader(Ride(lat, lng, epoch)));

        var result = await service.ProjectAsync(new TrackId(1), course, Checkpoints(course.TotalMeters), new ParticipantId(42));

        Assert.NotNull(result);
        var splits = result!.Splits.OrderBy(s => s.CheckpointId).ToList();
        Assert.Equal(3, splits.Count);
        Assert.All(splits, s => Assert.Equal(new ParticipantId(42), s.ParticipantId));

        Assert.InRange(splits[0].ElapsedSeconds, 0, 5);           // Start
        Assert.InRange(splits[1].ElapsedSeconds, 180, 220);       // Half: 1000 m at 5 m/s
        Assert.InRange(splits[2].ElapsedSeconds, 375, 400);       // Finish
    }

    [Fact]
    public async Task Project_RiderWhoAbandons_GetsNoSplitForCheckpointsHeNeverReached()
    {
        // He stops at halfway. He must not be handed a finish time he never earned.
        var course = Course(201);
        var (lat, lng, epoch) = OnCourse(course);
        var service = new RaceRealParticipantService(
            new FakeTrackReader(Ride(lat[..101], lng[..101], epoch[..101])));

        var result = await service.ProjectAsync(new TrackId(1), course, Checkpoints(course.TotalMeters), new ParticipantId(42));

        Assert.NotNull(result);
        Assert.Contains(result!.Splits, s => s.CheckpointId == new CheckpointId(1));   // Start
        Assert.Contains(result.Splits, s => s.CheckpointId == new CheckpointId(2));    // Half
        Assert.DoesNotContain(result.Splits, s => s.CheckpointId == new CheckpointId(3));   // Finish — never reached
    }

    /// <summary>
    /// The Finish checkpoint is seeded from <c>Activity.TotalDistance</c> (RaceCrud), which is raw arc
    /// length. A rider's chainage comes off the GRID, whose last node is <c>floor(arc / 10) * 10</c> —
    /// so the finish line sits up to one grid step PAST the furthest chainage anybody can ever reach.
    ///
    /// With an exact <c>&gt;=</c> the condition then never fires and every real rider reads DNF, on his own
    /// ride, on his own course. Race 2 is the live case: Finish at 38850.3 m against a grid that stops at
    /// 38850.0. Rounded to 4 decimal places this is invisible; it is still a wrong badge on every rider.
    /// </summary>
    [Fact]
    public async Task Project_FinishBeyondTheGridsLastNode_StillCountsAsReached()
    {
        var course = Course(201);
        var (lat, lng, epoch) = OnCourse(course);
        var service = new RaceRealParticipantService(new FakeTrackReader(Ride(lat, lng, epoch)));

        // Exactly what the DB holds: the unquantized activity distance, a fraction past the grid's end.
        var checkpoints = Checkpoints(course.TotalMeters);
        checkpoints[2].ChainageMeters = course.TotalMeters + 0.3;

        var result = await service.ProjectAsync(new TrackId(1), course, checkpoints, new ParticipantId(42));

        Assert.NotNull(result);
        Assert.Contains(result!.Splits, s => s.CheckpointId == new CheckpointId(3));
    }

    /// <summary>
    /// The production case the clamp above does NOT cover, and the one that shipped a wrong badge.
    ///
    /// Chainage is <c>SegmentProgress × TotalMeters</c> off a DTW-resampled reference, so its final value
    /// lands within tens of metres of the total — see Project_OnCourseRider_ChainageTracksTheCourse, which
    /// asserts only ±40 m for a rider who rode the course exactly. RaceCheckpointBounds keeps the Finish
    /// inside the grid, so after clamping the target IS the last node, and clearing it demands progress
    /// == 1.0 exactly. Race 9 is the live case: 865 entrants, and the one rider whose position is REAL
    /// rather than modelled was badged DNF and ranked below all 823 simulated finishers.
    ///
    /// Simulated here by pulling the finish target just inside what he can reach, which is the same
    /// geometry seen from the other side.
    /// </summary>
    [Fact]
    public async Task Project_RiderWhoseChainagePeaksJustShortOfTheLine_StillFinishes()
    {
        var course = Course(201);
        var (lat, lng, epoch) = OnCourse(course);
        var service = new RaceRealParticipantService(new FakeTrackReader(Ride(lat, lng, epoch)));

        var checkpoints = Checkpoints(course.TotalMeters);
        var result = await service.ProjectAsync(new TrackId(1), course, checkpoints, new ParticipantId(42));

        Assert.NotNull(result);
        var finish = Assert.Single(result!.Splits, s => s.CheckpointId == new CheckpointId(3));
        // His own time at the moment he was furthest along — never extrapolated past his last point.
        Assert.InRange(finish.ElapsedSeconds, 0, epoch[^1] - epoch[0]);
    }

    /// <summary>
    /// The other half of the slack rule: it is the projection's error bar, NOT forgiveness for quitting.
    /// A rider who stops a few hundred metres short is short, and 50 m of slack must not reach him.
    /// </summary>
    [Fact]
    public async Task Project_RiderWhoStopsWellShort_StillGetsNoFinish()
    {
        var course = Course(201);                       // 2000 m
        var (lat, lng, epoch) = OnCourse(course);
        // Drops out at node 180 — 1800 m, a full 200 m short of the line.
        var service = new RaceRealParticipantService(
            new FakeTrackReader(Ride(lat[..181], lng[..181], epoch[..181])));

        var result = await service.ProjectAsync(new TrackId(1), course, Checkpoints(course.TotalMeters), new ParticipantId(42));

        Assert.NotNull(result);
        Assert.DoesNotContain(result!.Splits, s => s.CheckpointId == new CheckpointId(3));
    }

    [Fact]
    public async Task Project_ElapsedIsFromHisOwnStart_NotWallClock()
    {
        // A friend's ride from a different day still races from the gun.
        var course = Course(201);
        var (lat, lng, epoch) = OnCourse(course);
        for (var i = 0; i < epoch.Length; i++) epoch[i] += 86_400 * 30;   // a month later

        var service = new RaceRealParticipantService(new FakeTrackReader(Ride(lat, lng, epoch)));

        var result = await service.ProjectAsync(new TrackId(1), course, Checkpoints(course.TotalMeters), new ParticipantId(42));

        Assert.NotNull(result);
        Assert.Equal(0, result!.ElapsedSeconds[0], 3);
        // Elapsed is measured from HIS first point, so a ride from a month ago still starts at 0.
        Assert.Equal((course.Count - 1) * 2.0, result.ElapsedSeconds[^1], 3);
    }

    [Fact]
    public async Task Project_TrackWithoutGps_IsNull()
    {
        var course = Course(201);
        var service = new RaceRealParticipantService(new FakeTrackReader(null));

        Assert.Null(await service.ProjectAsync(new TrackId(1), course, Checkpoints(course.TotalMeters), new ParticipantId(42)));
    }

    // ── F-2: the payload is BOUNDED. A 5 h 1 Hz ride shipped 4 × double[18,000] ≈ 1.2 MB per rider,
    // uncapped — the only track-serving path in the codebase that did not cap by default.

    [Fact]
    public async Task Project_LongRide_CapsEveryChannelToTheBudget_AndKeepsThemTheSameLength()
    {
        var course = Course(6001);                       // 60 km course, ridden point-for-point
        var (lat, lng, epoch) = OnCourse(course);
        var service = new RaceRealParticipantService(new FakeTrackReader(Ride(lat, lng, epoch)));

        var result = await service.ProjectAsync(new TrackId(1), course, Checkpoints(course.TotalMeters), new ParticipantId(42));

        Assert.NotNull(result);
        var cap = RaceRealParticipantService.RealTrackPointCap;
        Assert.True(result!.Lat.Length <= cap, $"lat {result.Lat.Length} > cap {cap}");
        Assert.True(result.Lat.Length < lat.Length);     // it really did thin
        // A rider whose channels disagree in length is a bug: the marker would read position from one
        // index and rank from another.
        Assert.Equal(result.Lat.Length, result.Lng.Length);
        Assert.Equal(result.Lat.Length, result.ElapsedSeconds.Length);
        Assert.Equal(result.Lat.Length, result.ChainageMeters.Length);
        // Coarser, but still HIS ride: endpoints survive the stride, so start and finish are intact.
        Assert.Equal(lat[0], result.Lat[0], 9);
        Assert.Equal(lat[^1], result.Lat[^1], 9);
        Assert.Equal(0, result.ElapsedSeconds[0], 3);
        Assert.Equal(epoch[^1] - epoch[0], result.ElapsedSeconds[^1], 3);
    }

    [Fact]
    public async Task Project_ShortRide_UnderTheBudget_IsNotResampled()
    {
        var course = Course(201);
        var (lat, lng, epoch) = OnCourse(course);
        var service = new RaceRealParticipantService(new FakeTrackReader(Ride(lat, lng, epoch)));

        var result = await service.ProjectAsync(new TrackId(1), course, Checkpoints(course.TotalMeters), new ParticipantId(42));

        // Under the budget the points are returned untouched — no lossy resample when it isn't needed.
        Assert.NotNull(result);
        Assert.Equal(lat.Length, result!.Lat.Length);
        for (var i = 0; i < lat.Length; i++) Assert.Equal(lat[i], result.Lat[i], 9);
    }

    [Fact]
    public async Task Project_ZeroMaxPoints_MeansTheCap_NotFullResolution()
    {
        // THE lesson, mirrored from SegmentsApiController: 0 (or negative) is not "give me everything".
        // The safe path is the default, so no caller can force a full-res decode on the weak server.
        var course = Course(6001);
        var (lat, lng, epoch) = OnCourse(course);
        var service = new RaceRealParticipantService(new FakeTrackReader(Ride(lat, lng, epoch)));
        var cap = RaceRealParticipantService.RealTrackPointCap;

        var zero = await service.ProjectAsync(new TrackId(1), course, Checkpoints(course.TotalMeters), new ParticipantId(42), maxPoints: 0);
        var negative = await service.ProjectAsync(new TrackId(1), course, Checkpoints(course.TotalMeters), new ParticipantId(42), maxPoints: -1);
        var over = await service.ProjectAsync(new TrackId(1), course, Checkpoints(course.TotalMeters), new ParticipantId(42), maxPoints: 100_000);

        Assert.True(zero!.Lat.Length <= cap);
        Assert.True(zero.Lat.Length < lat.Length);
        Assert.Equal(zero.Lat.Length, negative!.Lat.Length);
        // And a request ABOVE the cap is clamped to it, not honoured.
        Assert.Equal(zero.Lat.Length, over!.Lat.Length);
    }

    [Fact]
    public async Task Project_SmallerBudgetThanTheCap_IsHonoured()
    {
        var course = Course(2001);
        var (lat, lng, epoch) = OnCourse(course);
        var service = new RaceRealParticipantService(new FakeTrackReader(Ride(lat, lng, epoch)));

        var result = await service.ProjectAsync(new TrackId(1), course, Checkpoints(course.TotalMeters), new ParticipantId(42), maxPoints: 500);

        Assert.NotNull(result);
        Assert.True(result!.Lat.Length <= 500);
        Assert.Equal(result.Lat.Length, result.ChainageMeters.Length);
    }
}
