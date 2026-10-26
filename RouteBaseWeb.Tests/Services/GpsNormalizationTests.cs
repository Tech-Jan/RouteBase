using RouteBaseWeb.Models;
using RouteBaseWeb.Services;
using Xunit;

namespace RouteBaseWeb.Tests.Services;

/// <summary>
/// Unit tests for <see cref="GpsNormalizationService"/>.
/// All tests operate purely on in-memory GpsData — no database required.
/// </summary>
public class GpsNormalizationTests
{
    private readonly GpsNormalizationService _sut = new();

    // ── helpers ────────────────────────────────────────────────────────────────

    private static GpsData Pt(
        double? alt = null,
        double? speed = null,
        double? lat = null,
        double? lng = null,
        DateTime? utc = null,
        double? dist = null) => new()
    {
        Altitude    = alt,
        Speed       = speed,
        GpsLat      = lat,
        GpsLong     = lng,
        DateTimeUtc = utc,
        Distance    = dist,
    };

    private static DateTime T(int secondsFromStart) =>
        new DateTime(2025, 1, 1, 8, 0, 0, DateTimeKind.Utc).AddSeconds(secondsFromStart);

    // ── SmoothAltitudes ────────────────────────────────────────────────────────

    [Fact]
    public void SmoothAltitudes_EmptyList_DoesNotThrow()
    {
        // Should complete without error on an empty collection.
        _sut.SmoothAltitudes([]);
    }

    [Fact]
    public void SmoothAltitudes_SinglePoint_AltitudeUnchanged()
    {
        var pts = new List<GpsData> { Pt(alt: 500.0) };
        _sut.SmoothAltitudes(pts);
        Assert.Equal(500.0, pts[0].Altitude);
    }

    [Fact]
    public void SmoothAltitudes_ConstantAltitude_RemainsConstant()
    {
        // EMA of a constant signal is the constant itself.
        var pts = Enumerable.Range(0, 10).Select(_ => Pt(alt: 1000.0)).ToList();
        _sut.SmoothAltitudes(pts);
        foreach (var pt in pts)
            Assert.Equal(1000.0, pt.Altitude!.Value, precision: 6);
    }

    [Fact]
    public void SmoothAltitudes_IsolatedSpike_IsClamped()
    {
        // Surrounding points at 1000 m; one point jumps to 1080 m (> 30 m spike threshold).
        // After smoothing the spike point must be much closer to 1000 m than 1080 m.
        var pts = new List<GpsData>
        {
            Pt(alt: 1000.0),
            Pt(alt: 1000.0),
            Pt(alt: 1080.0), // spike
            Pt(alt: 1000.0),
            Pt(alt: 1000.0),
        };
        _sut.SmoothAltitudes(pts);
        Assert.True(pts[2].Altitude!.Value < 1040.0,
            $"Spike should be suppressed; got {pts[2].Altitude!.Value:F2}");
    }

    [Fact]
    public void SmoothAltitudes_GradualClimb_RemainsIncreasing()
    {
        // A real, gradual 5 m/step climb should survive smoothing — altitudes must still increase.
        var pts = Enumerable.Range(0, 10)
            .Select(i => Pt(alt: 1000.0 + i * 5.0))
            .ToList();
        _sut.SmoothAltitudes(pts);

        // Last point should be higher than the first after smoothing.
        Assert.True(pts[^1].Altitude > pts[0].Altitude,
            "Smoothed altitudes should still increase over a genuine climb");
    }

    [Fact]
    public void SmoothAltitudes_NullAltitudePoints_AreSkipped()
    {
        // Points without altitude must be left as null; EMA state carries across the gap.
        var pts = new List<GpsData>
        {
            Pt(alt: 1000.0),
            Pt(alt: null),   // no altitude — must remain null
            Pt(alt: 1000.0),
        };
        _sut.SmoothAltitudes(pts);
        Assert.Null(pts[1].Altitude);
        // First and last should still have values.
        Assert.NotNull(pts[0].Altitude);
        Assert.NotNull(pts[2].Altitude);
    }

    [Fact]
    public void SmoothAltitudes_AllNullAltitudes_DoesNotThrow()
    {
        var pts = new List<GpsData> { Pt(alt: null), Pt(alt: null) };
        _sut.SmoothAltitudes(pts);
        Assert.All(pts, p => Assert.Null(p.Altitude));
    }

    // ── ComputeStats ────────────────────────────────────────────────────────────

    [Fact]
    public void ComputeStats_EmptyList_ReturnsZeroStats()
    {
        var stats = _sut.ComputeStats([]);
        Assert.Equal(0, stats.TotalDistanceMeters);
        Assert.Equal(0, stats.TotalAscentMeters);
        Assert.Equal(0, stats.TotalDescentMeters);
        Assert.Equal(0, stats.TimeTotalSeconds);
        Assert.Null(stats.StartUtc);
    }

    [Fact]
    public void ComputeStats_SinglePoint_ReturnsZeroDistanceAndTime()
    {
        var pts = new List<GpsData> { Pt(lat: 47.0, lng: 11.0, utc: T(0)) };
        var stats = _sut.ComputeStats(pts);
        Assert.Equal(0, stats.TotalDistanceMeters);
        Assert.Equal(0, stats.TimeTotalSeconds);
    }

    [Fact]
    public void ComputeStats_FlatTrack_PositiveDistanceZeroAscent()
    {
        // Two points at the same altitude, ~111 m apart.
        var pts = new List<GpsData>
        {
            Pt(lat: 47.000, lng: 11.0, alt: 1000.0, utc: T(0)),
            Pt(lat: 47.001, lng: 11.0, alt: 1000.0, utc: T(10)),
        };
        var stats = _sut.ComputeStats(pts);
        Assert.True(stats.TotalDistanceMeters > 50, "Expected meaningful distance");
        Assert.Equal(0, stats.TotalAscentMeters);
        Assert.Equal(0, stats.TotalDescentMeters);
    }

    [Fact]
    public void ComputeStats_ClimbingTrack_PositiveAscentZeroDescent()
    {
        // Five points climbing 10 m each step (total 40 m gain over 4 segments).
        var pts = Enumerable.Range(0, 5)
            .Select(i => Pt(lat: 47.0 + i * 0.001, lng: 11.0, alt: 1000.0 + i * 10.0, utc: T(i * 10)))
            .ToList();
        var stats = _sut.ComputeStats(pts);
        Assert.True(stats.TotalAscentMeters > 0, "Expected positive ascent");
        Assert.Equal(0, stats.TotalDescentMeters, precision: 6);
    }

    [Fact]
    public void ComputeStats_DescendingTrack_PositiveDescentZeroAscent()
    {
        // Five points descending 10 m each step.
        var pts = Enumerable.Range(0, 5)
            .Select(i => Pt(lat: 47.0 + i * 0.001, lng: 11.0, alt: 1040.0 - i * 10.0, utc: T(i * 10)))
            .ToList();
        var stats = _sut.ComputeStats(pts);
        Assert.True(stats.TotalDescentMeters > 0, "Expected positive descent");
        Assert.Equal(0, stats.TotalAscentMeters, precision: 6);
    }

    [Fact]
    public void ComputeStats_SmallAltitudeDeltas_FilteredByNoiseThreshold()
    {
        // Altitude oscillates ±1 m (below the 2 m noise filter). Expect zero ascent/descent.
        var pts = new List<GpsData>
        {
            Pt(lat: 47.000, lng: 11.0, alt: 1000.0, utc: T(0)),
            Pt(lat: 47.001, lng: 11.0, alt: 1001.0, utc: T(10)),  // +1 m
            Pt(lat: 47.002, lng: 11.0, alt: 1000.0, utc: T(20)),  // -1 m
            Pt(lat: 47.003, lng: 11.0, alt: 1001.0, utc: T(30)),  // +1 m
        };
        var stats = _sut.ComputeStats(pts);
        // Noise accumulator never reaches 2 m threshold — no ascent or descent recorded.
        Assert.Equal(0, stats.TotalAscentMeters, precision: 6);
        Assert.Equal(0, stats.TotalDescentMeters, precision: 6);
    }

    [Fact]
    public void ComputeStats_MovingTime_OnlyCountsSegmentsAboveSpeedThreshold()
    {
        // Three segments: stationary (0 m/s), then moving (5 m/s), then stationary again.
        var pts = new List<GpsData>
        {
            Pt(lat: 47.000, lng: 11.0, speed: 0.0,  utc: T(0)),
            Pt(lat: 47.001, lng: 11.0, speed: 0.0,  utc: T(10)),  // segment 1: stationary
            Pt(lat: 47.002, lng: 11.0, speed: 5.0,  utc: T(20)),  // segment 2: moving (10 s)
            Pt(lat: 47.003, lng: 11.0, speed: 0.0,  utc: T(30)),  // segment 3: stationary
        };
        var stats = _sut.ComputeStats(pts);
        // Only segment 2 → 3 should be counted: speed[2]=5.0 > 0.5, dt = 10 s.
        Assert.Equal(10.0, stats.TimeMovingSeconds, precision: 6);
        Assert.Equal(30.0, stats.TimeTotalSeconds, precision: 6);
    }

    [Fact]
    public void ComputeStats_SpeedMax_IsHighestRecordedSpeed()
    {
        var pts = new List<GpsData>
        {
            Pt(speed: 3.0, utc: T(0)),
            Pt(speed: 7.5, utc: T(10)),
            Pt(speed: 2.0, utc: T(20)),
        };
        var stats = _sut.ComputeStats(pts);
        Assert.Equal(7.5, stats.SpeedMaxMs, precision: 6);
    }

    [Fact]
    public void ComputeStats_StartAndEndCoords_MatchFirstAndLastPoints()
    {
        var pts = new List<GpsData>
        {
            Pt(lat: 47.0, lng: 11.0, utc: T(0)),
            Pt(lat: 47.5, lng: 11.5, utc: T(60)),
        };
        var stats = _sut.ComputeStats(pts);
        Assert.Equal(47.0, stats.StartLat);
        Assert.Equal(11.0, stats.StartLng);
        Assert.Equal(47.5, stats.EndLat);
        Assert.Equal(11.5, stats.EndLng);
        Assert.Equal(T(0),  stats.StartUtc);
    }

    [Fact]
    public void ComputeStats_NullCoordMidPoint_NoDistanceSpike()
    {
        // Middle point has no fix (FIT record with no position). Pre-fix, legs to/from (0,0) added
        // ~13,000 km; now the null point is skipped for distance.
        var pts = new List<GpsData>
        {
            Pt(lat: 47.0, lng: 11.0, utc: T(0)),
            Pt(lat: null, lng: null, utc: T(1)),
            Pt(lat: 47.0 + 0.0009, lng: 11.0, utc: T(2)),   // ~100 m from the first fix
        };
        var stats = _sut.ComputeStats(pts);
        Assert.True(stats.TotalDistanceMeters < 300,
            $"expected no (0,0) spike, got {stats.TotalDistanceMeters} m");
    }

    [Fact]
    public void ComputeStats_LeadingNullCoords_StartCoordsFromFirstFix()
    {
        var pts = new List<GpsData>
        {
            Pt(lat: null, lng: null, utc: T(0)),
            Pt(lat: 47.2, lng: 11.3, utc: T(1)),
            Pt(lat: 47.4, lng: 11.5, utc: T(2)),
        };
        var stats = _sut.ComputeStats(pts);
        Assert.Equal(47.2, stats.StartLat);
        Assert.Equal(11.3, stats.StartLng);
        Assert.Equal(47.4, stats.EndLat);
        Assert.Equal(11.5, stats.EndLng);
    }

    [Fact]
    public void ComputeStats_TotalTime_IsWallClockSpan()
    {
        var pts = new List<GpsData>
        {
            Pt(utc: T(0)),
            Pt(utc: T(45)),
            Pt(utc: T(120)),
        };
        var stats = _sut.ComputeStats(pts);
        Assert.Equal(120.0, stats.TimeTotalSeconds, precision: 6);
    }

    [Fact]
    public void ComputeStats_SpeedAvg_IsTotalDistanceOverTotalTime()
    {
        // Two points ~111 m apart in 10 s → avg ≈ 11.1 m/s.
        var pts = new List<GpsData>
        {
            Pt(lat: 47.000, lng: 11.0, utc: T(0)),
            Pt(lat: 47.001, lng: 11.0, utc: T(10)),
        };
        var stats = _sut.ComputeStats(pts);
        double expected = stats.TotalDistanceMeters / 10.0;
        Assert.Equal(expected, stats.SpeedAvgMs, precision: 6);
    }

    // ── TrimTrailingDwell ────────────────────────────────────────────────────────

    /// <summary>
    /// Builds 1 Hz tracks with realistic geometry, since the dwell trim judges net progress over
    /// minutes — durations of a few seconds are below anything it can or should decide.
    /// </summary>
    private sealed class TrackBuilder
    {
        private const double MetreInDegreesLat = 1.0 / 111_320.0;

        private readonly List<GpsData> _pts = new();
        private readonly Random _rng = new(20260810);   // fixed seed: jitter must be reproducible
        private double _lat = 47.0;
        private double _cumulative;
        private int _second;

        /// Straight-line travel due north at a constant ground speed.
        public TrackBuilder Move(int seconds, double metresPerSecond)
        {
            for (int i = 0; i < seconds; i++)
            {
                Add(_lat);
                _lat += metresPerSecond * MetreInDegreesLat;
            }
            return this;
        }

        /// Parked: no net progress, but the fix wanders. This is the case a speed gate cannot see —
        /// consecutive wanders pile up path distance, so they imply ground speeds far above the
        /// 0.5 m/s moving threshold while the rider goes nowhere.
        public TrackBuilder Park(int seconds, double jitterMetres = 8.0)
        {
            double anchor = _lat;
            for (int i = 0; i < seconds; i++)
            {
                double offset = (_rng.NextDouble() * 2 - 1) * jitterMetres;
                Add(anchor + offset * MetreInDegreesLat);
            }
            _lat = anchor;
            return this;
        }

        /// A recording gap — lost fix indoors. Time passes with no points.
        public TrackBuilder Gap(int seconds)
        {
            _second += seconds;
            return this;
        }

        /// Distance is the cumulative path length over the emitted coordinates and Speed follows from
        /// it, the way finalization derives both — so jitter costs distance here exactly as it does live.
        private void Add(double lat)
        {
            double stepMetres = _pts.Count == 0
                ? 0.0
                : GeoUtils.HaversineDistance(_pts[^1].GpsLat!.Value, _pts[^1].GpsLong!.Value, lat, 11.0);
            double stepSeconds = _pts.Count == 0
                ? 1.0
                : (T(_second) - _pts[^1].DateTimeUtc!.Value).TotalSeconds;

            _cumulative += stepMetres;
            _pts.Add(Pt(alt: 500.0, speed: stepMetres / stepSeconds, lat: lat, lng: 11.0,
                        utc: T(_second), dist: _cumulative));
            _second++;
        }

        public List<GpsData> Build() => _pts;
    }

    [Fact]
    public void TrimTrailingDwell_DropsParkedTail_EndsAtTheArrival()
    {
        // 10 min of riding, then 10 min parked at the destination — the shape of every auto-recording,
        // which keeps running until the phone's idle watchdog fires.
        var pts = new TrackBuilder().Move(600, 5.0).Park(600).Build();

        _sut.TrimTrailingDwell(pts);

        // The track now ends at the arrival, not 10 minutes later.
        Assert.InRange(pts.Count, 560, 620);
        Assert.InRange((pts[^1].DateTimeUtc!.Value - T(0)).TotalSeconds, 560, 620);
    }

    [Fact]
    public void TrimTrailingDwell_ParkedTailJittersAboveTheMovingSpeedGate_StillTrimmed()
    {
        // The regression this rule exists for. Measured over 13 real auto-recordings, a parked phone's
        // GPS crosses the 0.5 m/s moving gate continuously, which is why the old speed-based trim
        // removed 0-16 s of a 2.5-19 min tail. Net progress sees through it.
        var pts = new TrackBuilder().Move(600, 5.0).Park(600).Build();
        GeoUtils.SmoothSpeeds(pts);   // exactly what finalization does before trimming

        // Precondition: the parked tail really does look "moving" to a speed threshold.
        int parkedLookingMoving = pts.Skip(600).Count(p => p.Speed is > 0.5);
        Assert.True(parkedLookingMoving > 100,
            $"expected a parked tail that defeats a speed gate, only {parkedLookingMoving} points cross it");

        _sut.TrimTrailingDwell(pts);

        Assert.InRange(pts.Count, 560, 620);
    }

    [Fact]
    public void TrimTrailingDwell_WalkedCooldown_NotTrimmed()
    {
        // A run that ends walking home at ~75 m/min for 13 min — slow, but real travel, and the case a
        // naive "slow means stopped" rule would destroy. Every point must survive.
        var pts = new TrackBuilder().Move(600, 3.0).Move(780, 1.25).Build();
        int before = pts.Count;

        _sut.TrimTrailingDwell(pts);

        Assert.Equal(before, pts.Count);
    }

    [Fact]
    public void TrimTrailingDwell_MidTrackStop_NotTrimmed()
    {
        // A café stop in the middle, then riding on to a moving finish. Only a dwell that reaches the
        // end of the track is an arrival, so this one must be left alone.
        var pts = new TrackBuilder().Move(300, 5.0).Park(300).Move(300, 5.0).Build();
        int before = pts.Count;

        _sut.TrimTrailingDwell(pts);

        Assert.Equal(before, pts.Count);
    }

    [Fact]
    public void TrimTrailingDwell_BriefPauseAtTheEnd_NotTrimmed()
    {
        // Stopping at a light for the last minute is a pause, not an arrival — below the dwell floor.
        var pts = new TrackBuilder().Move(600, 5.0).Park(60).Build();
        int before = pts.Count;

        _sut.TrimTrailingDwell(pts);

        Assert.Equal(before, pts.Count);
    }

    [Fact]
    public void TrimTrailingDwell_DropoutInTheParkedTail_StillTrimmed()
    {
        // Parking indoors loses the fix for a while (one real recording had a 92 s hole). The gap must
        // not read as movement and split the dwell.
        var pts = new TrackBuilder().Move(600, 5.0).Park(120).Gap(92).Park(240).Build();

        _sut.TrimTrailingDwell(pts);

        Assert.InRange(pts.Count, 560, 620);
    }

    [Fact]
    public void TrimTrailingDwell_AllStationary_LeftIntact()
    {
        // Never moved → don't trim to nothing.
        var pts = new TrackBuilder().Park(600).Build();

        _sut.TrimTrailingDwell(pts);

        Assert.Equal(600, pts.Count);
    }

    [Fact]
    public void TrimTrailingDwell_PointsWithoutFixes_LeftIntact()
    {
        // No coordinates means nothing can be judged — neither start nor break a dwell.
        var pts = Enumerable.Range(0, 300).Select(i => Pt(speed: 0.0, utc: T(i))).ToList();

        _sut.TrimTrailingDwell(pts);

        Assert.Equal(300, pts.Count);
    }

    // ── RejectPositionOutliers ───────────────────────────────────────────────────

    [Fact]
    public void RejectPositionOutliers_DropsIsolatedTeleport()
    {
        // Points ~8 m apart each second (~8 m/s), with one fix 5 km off route in between.
        var pts = new List<GpsData>
        {
            Pt(lat: 47.0000, lng: 11.0, utc: T(0)),
            Pt(lat: 47.0001, lng: 11.0, utc: T(1)),
            Pt(lat: 47.0500, lng: 11.0, utc: T(2)),   // teleport — ~5.5 km in 1 s
            Pt(lat: 47.0002, lng: 11.0, utc: T(3)),
            Pt(lat: 47.0003, lng: 11.0, utc: T(4)),
        };
        _sut.RejectPositionOutliers(pts);
        Assert.Equal(4, pts.Count);
        Assert.DoesNotContain(pts, p => p.GpsLat == 47.0500);
    }

    [Fact]
    public void RejectPositionOutliers_KeepsPlausibleFastMovement()
    {
        // ~30 m/s (108 km/h) — fast, but under the 40 m/s ceiling. Nothing should be dropped.
        var pts = new List<GpsData>
        {
            Pt(lat: 47.0000, lng: 11.0, utc: T(0)),
            Pt(lat: 47.00027, lng: 11.0, utc: T(1)),
            Pt(lat: 47.00054, lng: 11.0, utc: T(2)),
        };
        _sut.RejectPositionOutliers(pts);
        Assert.Equal(3, pts.Count);
    }

    [Fact]
    public void RejectPositionOutliers_ReanchorsAfterSustainedJump()
    {
        // A real position shift (GPS re-acquired elsewhere) that persists must not delete the whole tail:
        // after MaxConsecutiveDrops the filter re-anchors and keeps the new segment.
        var pts = new List<GpsData> { Pt(lat: 47.0000, lng: 11.0, utc: T(0)) };
        for (int i = 1; i <= 6; i++)
            pts.Add(Pt(lat: 48.0000 + i * 0.0001, lng: 11.0, utc: T(i)));   // far away but internally consistent

        _sut.RejectPositionOutliers(pts);
        // Anchor + at most MaxConsecutiveDrops removed, then the rest re-anchored and kept.
        Assert.True(pts.Count >= 4, $"expected re-anchor to retain the tail, got {pts.Count}");
    }

    [Fact]
    public void RejectPositionOutliers_ShortTrack_Untouched()
    {
        var pts = new List<GpsData>
        {
            Pt(lat: 47.0, lng: 11.0, utc: T(0)),
            Pt(lat: 48.0, lng: 11.0, utc: T(1)),
        };
        _sut.RejectPositionOutliers(pts);
        Assert.Equal(2, pts.Count);
    }
}
