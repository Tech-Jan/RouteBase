using RouteBaseWeb.Services.Matching;
using RouteBaseWeb.Services.Tracks;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Matching;

/// <summary>
/// Pins EffortMathCore's offset pair-search (S8) and aggregates (S9) against the SQL catalog,
/// bug-for-bug. Pure (no DB) — the whole point of TE-15's I/O-free extraction.
/// </summary>
public class EffortMathCoreTests
{
    // Builds a decoded track from ids + coords, with optional per-channel arrays. Ordinal ≡ GPS_ID
    // post re-key: place each fixture point (and its channel samples) AT its id ordinal, NaN-pad the
    // gaps. NaN pads are inert (excluded from bands, aggregates, offsets). TimeEpochS defaults to
    // all-NaN unless supplied; a supplied time[] overrides the NaN fill at id positions.
    private static DecodedTrack Track(
        int[] ids, (double lat, double lng)[] pts,
        double[]? dist = null, double[]? time = null, double[]? alt = null,
        double[]? speed = null, double[]? hr = null, double[]? cad = null, double[]? power = null)
    {
        int n = ids[^1] + 1;                                  // ids ascending in every fixture
        double[] lat = Filled(n), lng = Filled(n), t = Filled(n);
        double[]? d  = dist  is null ? null : Filled(n);
        double[]? a  = alt   is null ? null : Filled(n);
        double[]? sp = speed is null ? null : Filled(n);
        double[]? h  = hr    is null ? null : Filled(n);
        double[]? c  = cad   is null ? null : Filled(n);
        double[]? p  = power is null ? null : Filled(n);
        for (int k = 0; k < ids.Length; k++)
        {
            int id = ids[k];
            lat[id] = pts[k].lat;
            lng[id] = pts[k].lng;
            if (dist  != null) d![id]  = dist[k];
            if (time  != null) t[id]   = time[k];
            if (alt   != null) a![id]  = alt[k];
            if (speed != null) sp![id] = speed[k];
            if (hr    != null) h![id]  = hr[k];
            if (cad   != null) c![id]  = cad[k];
            if (power != null) p![id]  = power[k];
        }
        return DecodedTrackExtensions.FromDoubleChannels(t, lat, lng, a, d, sp, h, null, c, p);
    }

    private static double[] Filled(int n)
    {
        var arr = new double[n];
        Array.Fill(arr, double.NaN);
        return arr;
    }

    private static EffortWindow Win(int entryGuessed, int exitGuessed) =>
        new(GpsInSegmentId: 7, TrackId: 1, entryGuessed, exitGuessed);

    // =====================================================================
    // Offsets — pair search (S8)
    // =====================================================================

    [Fact]
    public void Offsets_LoopSegment_PicksClosingPair_NotDegenerateNearZeroPair()
    {
        // Loop: start pin == end pin (47.000). Runner closes the loop. The near-zero degenerate
        // pair (g1→g2, traversal 5 m < 10% of 100) is guard-rejected; the closing pair g1→g4 wins.
        var track = Track(
            new[] { 1, 2, 3, 4 },
            new[] { (47.000, 11.000), (47.000, 11.000), (47.050, 11.000), (47.000, 11.000) },
            dist: new[] { 0.0, 5.0, 50.0, 100.0 });

        var r = EffortMathCore.ComputeOffsets(Win(1, 4), track,
            pinStartLat: 47.000, pinStartLng: 11.000, pinEndLat: 47.000, pinEndLng: 11.000,
            segmentDistance: 100);

        Assert.Equal(1, r.EntryCalc);
        Assert.Equal(4, r.ExitCalc);          // closing pair, not the degenerate g1→g2 exit
    }

    [Fact]
    public void Offsets_MultiPass_PicksSecondStartCrossing()
    {
        // Two touches of the start pin (g1, g3); only the second (g3) yields a traversal matching
        // segment_distance 1000, so the pair search rejects the first crossing.
        var track = Track(
            new[] { 1, 2, 3, 4 },
            new[] { (47.000, 11.000), (47.002, 11.000), (47.000, 11.000), (47.010, 11.000) },
            dist: new[] { 0.0, 50.0, 100.0, 1100.0 });

        var r = EffortMathCore.ComputeOffsets(Win(1, 4), track,
            47.000, 11.000, 47.010, 11.000, segmentDistance: 1000);

        Assert.Equal(3, r.EntryCalc);         // second start crossing
        Assert.Equal(4, r.ExitCalc);
    }

    [Fact]
    public void Offsets_NullDistances_BypassTenPctGuard_Score9e15()
    {
        // No distance channel → cum NULL → guard bypassed, every pair scores 9e15. Winner falls to
        // the offset-sum/id tie-break: g1→g2 (lowest exit id) over g1→g3.
        var track = Track(
            new[] { 1, 2, 3 },
            new[] { (47.000, 11.000), (47.010, 11.000), (47.010, 11.000) });   // no dist

        var r = EffortMathCore.ComputeOffsets(Win(1, 3), track,
            47.000, 11.000, 47.010, 11.000, segmentDistance: 1000);

        Assert.Equal(1, r.EntryCalc);         // a pair WAS chosen (guard bypassed) …
        Assert.Equal(2, r.ExitCalc);          // … lowest exit GPS_ID on the 9e15/sum tie
    }

    [Fact]
    public void Offsets_NullSegmentDistance_ExcludesMeasuredPairs()
    {
        // NULL segment_distance + present cums → every pair excluded (`>= NULL` is unknown) → no
        // winner → fallback. Guessed entry (g1, 0.005 off the pin) is used, not the band-best g2.
        var track = Track(
            new[] { 1, 2, 3 },
            new[] { (47.005, 11.000), (47.000, 11.000), (47.010, 11.000) },
            dist: new[] { 0.0, 100.0, 500.0 });

        var r = EffortMathCore.ComputeOffsets(Win(1, 3), track,
            47.000, 11.000, 47.010, 11.000, segmentDistance: null);

        Assert.Equal(1, r.EntryCalc);                              // fallback → guessed id
        Assert.Equal(SqliteCompat.Round5(0.005), r.OffsetEntry);   // raw pin distance at guessed g1
        Assert.Equal(3, r.ExitCalc);
        Assert.Equal(0.0, r.OffsetExit);
    }

    [Fact]
    public void Offsets_NoPair_FallbackUsesGuessedIdsAndRawPinOffsets()
    {
        // Single band member each side → no strict entry<exit pair → fallback with raw pin offsets.
        var track = Track(
            new[] { 1, 2 },
            new[] { (47.001, 11.000), (47.002, 11.000) },
            dist: new[] { 0.0, 5.0 });

        var r = EffortMathCore.ComputeOffsets(Win(1, 2), track,
            47.000, 11.000, 47.000, 11.000, segmentDistance: 100);

        Assert.Equal(1, r.EntryCalc);
        Assert.Equal(2, r.ExitCalc);
        Assert.Equal(SqliteCompat.Round5(0.001), r.OffsetEntry);   // |47.000 − 47.001|
        Assert.Equal(SqliteCompat.Round5(0.002), r.OffsetExit);    // |47.000 − 47.002|
    }

    [Fact]
    public void Offsets_FallbackMissingGpsId_NullOffset()
    {
        // Guessed entry GPS_ID 99 does not exist in the track → fallback entry offset is NULL;
        // the guessed window itself is empty so the search short-circuits to the fallback.
        var track = Track(
            new[] { 1, 2 },
            new[] { (47.001, 11.000), (47.002, 11.000) });

        var r = EffortMathCore.ComputeOffsets(Win(99, 2), track,
            47.000, 11.000, 47.000, 11.000, segmentDistance: 100);

        Assert.Equal(99, r.EntryCalc);
        Assert.Null(r.OffsetEntry);                                // GPS_ID 99 absent → NULL
        Assert.Equal(2, r.ExitCalc);
        Assert.Equal(SqliteCompat.Round5(0.002), r.OffsetExit);    // id 2 present → |47.000 − 47.002|
    }

    [Fact]
    public void Offsets_TieBreak_OffsetSumThenEntryExitId()
    {
        // All admissible pairs score 0 (traversal 1000 == segment_distance). Offset-sum decides
        // first (g2 beats lower-id g1 because its sum 0 < 0.0001), then exit id (g3 < g4).
        var track = Track(
            new[] { 1, 2, 3, 4 },
            new[] { (47.0001, 11.000), (47.000, 11.000), (47.010, 11.000), (47.010, 11.000) },
            dist: new[] { 0.0, 0.0, 1000.0, 1000.0 });

        var r = EffortMathCore.ComputeOffsets(Win(1, 4), track,
            47.000, 11.000, 47.010, 11.000, segmentDistance: 1000);

        Assert.Equal(2, r.EntryCalc);         // offset-sum 0 (g2) beats lower GPS_ID g1 (sum 0.0001)
        Assert.Equal(3, r.ExitCalc);          // exit-id tie-break: g3 < g4
        Assert.Equal(0.0, r.OffsetEntry);
        Assert.Equal(0.0, r.OffsetExit);
    }

    [Fact]
    public void Offsets_BandBoundary_DEqualsMinPlus0002_Included()
    {
        // g2 sits exactly at min_d + 0.0002 (min_d = 0 at the start pin) → included in the band, and
        // its pair (traversal 1000) beats g1's (traversal 1100) → g2 wins, proving inclusion.
        var track = Track(
            new[] { 1, 2, 3 },
            new[] { (47.000, 11.000), (47.0002, 11.000), (47.010, 11.000) },
            dist: new[] { 0.0, 100.0, 1100.0 });

        var r = EffortMathCore.ComputeOffsets(Win(1, 3), track,
            47.000, 11.000, 47.010, 11.000, segmentDistance: 1000);

        Assert.Equal(2, r.EntryCalc);                              // boundary point selected
        Assert.Equal(SqliteCompat.Round5(0.0002), r.OffsetEntry);
        Assert.Equal(3, r.ExitCalc);
    }

    // =====================================================================
    // Aggregates (S9)
    // =====================================================================

    [Fact]
    public void Aggregates_ElevationSumsOnlyPositiveDeltas_FirstAndNullGapsZero()
    {
        // alt [100,105,NaN,108,110]: first→0, +5, NaN gap→0 (both sides), +2. Sum = 7.
        var track = Track(
            new[] { 1, 2, 3, 4, 5 },
            new[] { (47.0, 11.0), (47.0, 11.0), (47.0, 11.0), (47.0, 11.0), (47.0, 11.0) },
            alt: new[] { 100.0, 105.0, double.NaN, 108.0, 110.0 });

        var r = EffortMathCore.ComputeAggregates(new AggregateWindow(7, 1, 1, 5), track);

        Assert.NotNull(r);
        Assert.Equal(7.0, r!.Elev);
        Assert.Equal(0.0, r.Dist);            // no distance channel → COALESCE 0
        Assert.Equal(0.0, r.TimeS);           // no timestamps → COALESCE 0
    }

    [Fact]
    public void Aggregates_EffortTime_UsesJuliandayReplica_MinMaxNotFirstLast()
    {
        double baseEpoch = (new DateTime(2021, 6, 15, 10, 0, 0, DateTimeKind.Utc) - DateTime.UnixEpoch).TotalSeconds;
        // Non-monotonic under GPS_ID order: min = base, max = base+1815; first/last are +100/+50.
        var time = new[] { baseEpoch + 100, baseEpoch + 0, baseEpoch + 1815, baseEpoch + 50 };
        var track = Track(
            new[] { 1, 2, 3, 4 },
            new[] { (47.0, 11.0), (47.0, 11.0), (47.0, 11.0), (47.0, 11.0) },
            time: time);

        var r = EffortMathCore.ComputeAggregates(new AggregateWindow(7, 1, 1, 4), track);

        double expected = SqliteCompat.EffortTimeSeconds(baseEpoch, baseEpoch + 1815);
        Assert.NotNull(r);
        Assert.Equal(expected, r!.TimeS);                          // julianday replica over MIN/MAX
        Assert.NotEqual(1815.0, r.TimeS);                          // not the exact integer diff
        Assert.NotEqual(time[^1] - time[0], r.TimeS);              // not last − first (−50)
    }

    [Fact]
    public void Aggregates_HrAvg_ExcludesZeroAndNull_NullWhenNone()
    {
        var withHr = Track(
            new[] { 1, 2, 3, 4 },
            new[] { (47.0, 11.0), (47.0, 11.0), (47.0, 11.0), (47.0, 11.0) },
            hr: new[] { 0.0, double.NaN, 120.0, 140.0 });
        var r1 = EffortMathCore.ComputeAggregates(new AggregateWindow(7, 1, 1, 4), withHr);
        Assert.Equal(130.0, r1!.HrAvg);       // (120 + 140) / 2 — 0 and NULL excluded

        var noHr = Track(
            new[] { 1, 2 },
            new[] { (47.0, 11.0), (47.0, 11.0) },
            hr: new[] { 0.0, 0.0 });
        var r2 = EffortMathCore.ComputeAggregates(new AggregateWindow(7, 1, 1, 2), noHr);
        Assert.Null(r2!.HrAvg);               // no positive samples → NULL (no COALESCE)
    }

    [Fact]
    public void Aggregates_CadenceAvg_OverPowerPresentPoints_NullCadenceCountsZero()
    {
        // power on idx2,3; cadence 90 at a no-power point (excluded), NULL at a power point (→0),
        // 80 at a power point → cadence_avg = (0 + 80) / COUNT(power)=2 = 40; power_avg = 225.
        var track = Track(
            new[] { 1, 2, 3, 4 },
            new[] { (47.0, 11.0), (47.0, 11.0), (47.0, 11.0), (47.0, 11.0) },
            cad:   new[] { 90.0, double.NaN, double.NaN, 80.0 },
            power: new[] { double.NaN, double.NaN, 200.0, 250.0 });

        var r = EffortMathCore.ComputeAggregates(new AggregateWindow(7, 1, 1, 4), track);
        Assert.Equal(40.0, r!.CadenceAvg);
        Assert.Equal(225.0, r.PowerAvg);

        var noPower = Track(
            new[] { 1, 2 },
            new[] { (47.0, 11.0), (47.0, 11.0) },
            cad: new[] { 70.0, 80.0 });        // cadence present but no power
        var r2 = EffortMathCore.ComputeAggregates(new AggregateWindow(7, 1, 1, 2), noPower);
        Assert.Null(r2!.CadenceAvg);           // COUNT(power)=0 → NULL
        Assert.Null(r2.PowerAvg);
    }

    [Fact]
    public void Aggregates_EmptyWindow_ReturnsNull()
    {
        // Window [5..9] lies wholly past the track end (ordinals 0..1) → OrdinalRange empty →
        // null (no write, S9.10). The old "window inside a GPS_ID gap" case is unrepresentable
        // under dense ordinals (post re-key there are no gaps); beyond-end is the reachable empty case.
        var track = Track(new[] { 0, 1 }, new[] { (47.0, 11.0), (47.0, 11.0) });
        Assert.Null(EffortMathCore.ComputeAggregates(new AggregateWindow(7, 1, 5, 9), track));
    }

    [Fact]
    public void Aggregates_SpeedAvg_ZeroNotNull_WhenNoSpeeds()
    {
        var noSpeed = Track(new[] { 1, 2 }, new[] { (47.0, 11.0), (47.0, 11.0) });
        var r1 = EffortMathCore.ComputeAggregates(new AggregateWindow(7, 1, 1, 2), noSpeed);
        Assert.NotNull(r1);
        Assert.Equal(0.0, r1!.SpeedAvg);       // COALESCE(AVG(speed), 0) → 0, not NULL

        var withSpeed = Track(new[] { 1, 2 }, new[] { (47.0, 11.0), (47.0, 11.0) },
            speed: new[] { 10.0, 20.0 });
        var r2 = EffortMathCore.ComputeAggregates(new AggregateWindow(7, 1, 1, 2), withSpeed);
        Assert.Equal(15.0, r2!.SpeedAvg);
    }
}
