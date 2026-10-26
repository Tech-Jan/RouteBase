using RouteBaseWeb.Services.Photos;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Photos;

/// <summary>
/// Pins the pure matcher (Phase-1 §3/§3a): time containment is the only auto-assign signal; GPS only
/// finds/annotates/positions. Covers the three §11 sample classes (Pixel time+GPS, WhatsApp time-only,
/// Signal stripped) plus ambiguous / GPS-only / no-match branches and along-track positioning.
/// </summary>
public class PhotoMatchServiceTests
{
    private static readonly DateTime Base = new(2026, 6, 23, 19, 0, 0, DateTimeKind.Utc);

    // A short straight track heading north from (47.90, 8.00): one point per minute, ~111 m spacing.
    private static IReadOnlyList<TrackSample> Track(int points = 10)
    {
        var list = new List<TrackSample>();
        for (int i = 0; i < points; i++)
        {
            long t = ToEpoch(Base.AddMinutes(i));
            list.Add(new TrackSample(t, 47.90 + i * 0.001, 8.00, i * 111.0));
        }
        return list;
    }

    // Out-and-back at 2-min spacing (total span 18 min > the ±10 min window, so the two passes through the
    // origin are separable in time). Point 0 and point 9 are co-located at the origin; point 9's timestamp
    // is 18 min after Base — outside the window centred on point 0's time, and vice-versa.
    private static IReadOnlyList<TrackSample> OutAndBackTrack()
    {
        var list = new List<TrackSample>();
        for (int i = 0; i <= 5; i++)  // outbound: 0..5
            list.Add(new TrackSample(ToEpoch(Base.AddMinutes(2 * i)), 47.90 + i * 0.001, 8.00, i * 111.0));
        for (int i = 1; i <= 4; i++)  // return: 6..9, mirrors 4..1 spatially (point 9 back at the origin)
            list.Add(new TrackSample(ToEpoch(Base.AddMinutes(2 * (5 + i))), 47.90 + (4 - i) * 0.001, 8.00, (5 + i) * 111.0));
        return list;
    }

    private static long ToEpoch(DateTime utc) =>
        (long)(DateTime.SpecifyKind(utc, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalSeconds;

    private static PhotoMatchActivity Activity(
        int id, DateTime start, DateTime end, IReadOnlyList<TrackSample>? track = null) =>
        new(id, $"Activity {id}", start, end, TzOffsetHours: 2, track);

    // ── auto-match (single time containment) ──────────────────────────────────

    [Fact]
    public void SingleTimeContainment_NoTrack_AutoMatchWithoutPosition()
    {
        var act = Activity(1, Base, Base.AddHours(1));
        var result = PhotoMatchService.Match(Base.AddMinutes(30), null, null, new[] { act });

        var am = Assert.IsType<PhotoMatchResult.AutoMatch>(result);
        Assert.Equal(1, am.TrackId);
        Assert.Null(am.TrackPositionM);
    }

    [Fact]
    public void Pixel_TimeAndGps_AutoMatch_PositionAtNearestPoint()
    {
        // Photo near the 4th track point (index 3 → distance 333 m); has both time and GPS.
        var act = Activity(1, Base, Base.AddHours(1), Track());
        var result = PhotoMatchService.Match(Base.AddMinutes(30), 47.9031, 8.0, new[] { act });

        var am = Assert.IsType<PhotoMatchResult.AutoMatch>(result);
        Assert.Equal(1, am.TrackId);
        Assert.Equal(333.0, am.TrackPositionM!.Value, 0);
    }

    [Fact]
    public void WhatsApp_TimeOnly_AutoMatch_PositionInterpolatedByTime()
    {
        // Time falls halfway between point 2 (t+2min, 222 m) and point 3 (t+3min, 333 m); no GPS.
        var act = Activity(1, Base, Base.AddHours(1), Track());
        var result = PhotoMatchService.Match(Base.AddSeconds(150), null, null, new[] { act });

        var am = Assert.IsType<PhotoMatchResult.AutoMatch>(result);
        Assert.Equal(277.5, am.TrackPositionM!.Value, 1); // midpoint of 222 and 333
    }

    [Theory]
    [InlineData(0)]      // exactly at StartUtc
    [InlineData(3600)]   // exactly at EndUtc
    public void TimeBoundary_IsInclusive(int secondsFromStart)
    {
        var act = Activity(1, Base, Base.AddHours(1));
        var result = PhotoMatchService.Match(Base.AddSeconds(secondsFromStart), null, null, new[] { act });
        Assert.IsType<PhotoMatchResult.AutoMatch>(result);
    }

    // ── ambiguous (multiple time containment) ─────────────────────────────────

    [Fact]
    public void MultipleTimeContainment_Ambiguous_ListsAllCandidates()
    {
        var a1 = Activity(1, Base, Base.AddHours(2));
        var a2 = Activity(2, Base.AddMinutes(10), Base.AddHours(1));
        var result = PhotoMatchService.Match(Base.AddMinutes(30), null, null, new[] { a1, a2 });

        var amb = Assert.IsType<PhotoMatchResult.Ambiguous>(result);
        Assert.Equal(new[] { 1, 2 }, amb.Candidates.Select(c => c.TrackId).OrderBy(x => x));
    }

    [Fact]
    public void MultipleTimeContainment_WithGps_AnnotatesDistance()
    {
        var a1 = Activity(1, Base, Base.AddHours(2), Track());
        var a2 = Activity(2, Base.AddMinutes(10), Base.AddHours(1), Track());
        var result = PhotoMatchService.Match(Base.AddMinutes(30), 47.90, 8.00, new[] { a1, a2 });

        var amb = Assert.IsType<PhotoMatchResult.Ambiguous>(result);
        Assert.All(amb.Candidates, c => Assert.NotNull(c.GpsDistanceM));
    }

    // ── GPS-only candidates (no time match, photo has GPS) ────────────────────

    [Fact]
    public void NoTimeMatch_GpsNear_ReturnsGpsCandidatesSortedByDistance()
    {
        // Photo GPS sits on the near track but its capture time is outside both ranges.
        var near = Activity(1, Base.AddHours(5), Base.AddHours(6), Track());     // passes through the point
        var far  = Activity(2, Base.AddHours(5), Base.AddHours(6),
                            new[] { new TrackSample(0, 10.0, 10.0, 0) });         // ~far away

        var result = PhotoMatchService.Match(Base.AddHours(10), 47.90, 8.00, new[] { near, far });

        var gps = Assert.IsType<PhotoMatchResult.GpsCandidates>(result);
        Assert.Single(gps.Candidates);
        Assert.Equal(1, gps.Candidates[0].TrackId);
    }

    [Fact]
    public void NoTime_GpsNear_ReturnsGpsCandidates()
    {
        var act = Activity(1, Base, Base.AddHours(1), Track());
        var result = PhotoMatchService.Match(photoUtc: null, 47.901, 8.00, new[] { act });
        Assert.IsType<PhotoMatchResult.GpsCandidates>(result);
    }

    // ── unmatched ─────────────────────────────────────────────────────────────

    [Fact]
    public void Signal_NoTimeNoGps_Unmatched()
    {
        var act = Activity(1, Base, Base.AddHours(1), Track());
        var result = PhotoMatchService.Match(null, null, null, new[] { act });
        Assert.IsType<PhotoMatchResult.Unmatched>(result);
    }

    [Fact]
    public void NoTimeMatch_GpsFarFromEveryTrack_Unmatched()
    {
        var act = Activity(1, Base, Base.AddHours(1), Track());
        var result = PhotoMatchService.Match(Base.AddHours(10), 10.0, 10.0, new[] { act });
        Assert.IsType<PhotoMatchResult.Unmatched>(result);
    }

    [Fact]
    public void NoCandidates_Unmatched()
    {
        var result = PhotoMatchService.Match(Base, 47.90, 8.00, Array.Empty<PhotoMatchActivity>());
        Assert.IsType<PhotoMatchResult.Unmatched>(result);
    }

    // ── positioning helpers ───────────────────────────────────────────────────

    [Fact]
    public void InterpolateLatLngAtTime_ReturnsMidpoint()
    {
        var latLng = PhotoMatchService.InterpolateLatLngAtTime(Base.AddSeconds(30), Track());
        Assert.NotNull(latLng);
        Assert.Equal(47.9005, latLng!.Value.Lat, 4); // halfway between point 0 and 1
    }

    [Fact]
    public void InterpolateLatLngAtTime_NoTime_Null()
    {
        Assert.Null(PhotoMatchService.InterpolateLatLngAtTime(null, Track()));
    }

    [Fact]
    public void ResolveAlongTrackPositionM_EmptyTrack_Null()
    {
        Assert.Null(PhotoMatchService.ResolveAlongTrackPositionM(Base, 47.9, 8.0, Array.Empty<TrackSample>()));
    }

    // ── MP-02: time-windowed nearest disambiguates loop/out-and-back passes ────

    [Fact]
    public void ResolveAlongTrackPositionM_OutAndBack_PrefersTimePlausiblePass()
    {
        var track = OutAndBackTrack();
        // Photo GPS sits at the origin (co-located with both point 0 and point 9), but its capture time
        // matches point 9 (the return pass through the same spot) — time-windowing must prefer point 9.
        var pos = PhotoMatchService.ResolveAlongTrackPositionM(
            Base.AddMinutes(18), 47.9001, 8.0001, track);
        Assert.Equal(track[9].DistanceM, pos);
    }

    [Fact]
    public void ResolveAlongTrackPositionM_OutAndBack_NoTime_FallsBackToPureSpatialNearest()
    {
        var track = OutAndBackTrack();
        var pos = PhotoMatchService.ResolveAlongTrackPositionM(null, 47.9001, 8.0001, track);
        // No time to disambiguate → whichever of point 0 / point 9 is spatially closest wins (unchanged
        // pre-existing behavior); assert it resolves to one of the two co-located points, not a fixed index.
        Assert.True(pos == track[0].DistanceM || pos == track[9].DistanceM);
    }

    // ── MP-04: boundary-condition coverage (no code change, pins existing behavior) ──

    [Fact]
    public void ResolveAlongTrackPositionM_TimeOnly_PhotoBeforeTrackStart_ClampsToFirstPoint()
    {
        var track = Track();
        var pos = PhotoMatchService.ResolveAlongTrackPositionM(Base.AddMinutes(-10), null, null, track);
        Assert.Equal(track[0].DistanceM, pos);
    }

    [Fact]
    public void ResolveAlongTrackPositionM_TimeOnly_PhotoAfterTrackEnd_ClampsToLastPoint()
    {
        var track = Track();
        var pos = PhotoMatchService.ResolveAlongTrackPositionM(Base.AddMinutes(100), null, null, track);
        Assert.Equal(track[^1].DistanceM, pos);
    }

    [Fact]
    public void ResolveAlongTrackPositionM_SinglePointTrack_ReturnsThatPoint()
    {
        var track = new[] { new TrackSample(ToEpoch(Base), 47.90, 8.00, 42.0) };
        Assert.Equal(42.0, PhotoMatchService.ResolveAlongTrackPositionM(Base, 47.9, 8.0, track));   // GPS branch
        Assert.Equal(42.0, PhotoMatchService.ResolveAlongTrackPositionM(Base, null, null, track));  // time branch
    }

    [Fact]
    public void SingleTimeContainment_NullTimeElapsed_TreatedAsZeroWidthWindow()
    {
        // null TimeElapsed collapses [StartUtc, EndUtc] to zero width (StartUtc == EndUtc).
        var act = Activity(1, Base, Base);
        Assert.IsType<PhotoMatchResult.AutoMatch>(
            PhotoMatchService.Match(Base, null, null, new[] { act }));            // exactly at the instant
        Assert.IsType<PhotoMatchResult.Unmatched>(
            PhotoMatchService.Match(Base.AddSeconds(1), null, null, new[] { act })); // one second off
    }

    // ── ResolveLocation (single stored location + provenance) ─────────────────

    [Fact]
    public void ResolveLocation_ExifGps_Wins()
    {
        var (lat, lng, src) = PhotoMatchService.ResolveLocation(
            hasExifGps: true, exifLat: 47.5, exifLng: 8.5, photoUtc: Base.AddSeconds(30), Track());
        Assert.Equal("exif", src);
        Assert.Equal(47.5, lat);
        Assert.Equal(8.5, lng);
    }

    [Fact]
    public void ResolveLocation_NoGpsButTimeAndTrack_Interpolated()
    {
        var (lat, lng, src) = PhotoMatchService.ResolveLocation(
            hasExifGps: false, exifLat: null, exifLng: null, photoUtc: Base.AddSeconds(30), Track());
        Assert.Equal("interpolated", src);
        Assert.Equal(47.9005, lat!.Value, 4); // midpoint of points 0 and 1
        Assert.NotNull(lng);
    }

    [Fact]
    public void ResolveLocation_NoGpsNoTime_None()
    {
        var (lat, lng, src) = PhotoMatchService.ResolveLocation(
            hasExifGps: false, exifLat: null, exifLng: null, photoUtc: null, Track());
        Assert.Equal("none", src);
        Assert.Null(lat);
        Assert.Null(lng);
    }
}
