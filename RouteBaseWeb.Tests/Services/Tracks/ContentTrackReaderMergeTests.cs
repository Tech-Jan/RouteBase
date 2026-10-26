using RouteBaseWeb.Services.Tracks;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Tracks;

/// <summary>
/// PERF-P1: <see cref="ContentTrackReader.Merge"/> now copies the already-quantized head verbatim and
/// quantizes only the small staging tail, instead of dequantizing→requantizing the whole head through a
/// double-array round-trip on every uncached live poll. These pins assert the new path is
/// (a) VALUE-IDENTICAL to that old round-trip across the tricky cases, and (b) allocates far less.
/// </summary>
public class ContentTrackReaderMergeTests
{
    private const double HeadFirstEpoch = 1_735_689_600; // 2025-01-01Z, seconds

    // ── Value-identity across the cases the finding names ──────────────────

    [Fact]
    public void EmptyHead_TailOnly_MatchesRoundTrip()
        => AssertIdentical(null, BuildTail(4, HeadFirstEpoch));

    [Fact]
    public void HeadOnly_EmptyTail_MatchesRoundTrip()
        => AssertIdentical(BuildFullHead(5), []);

    [Fact]
    public void HeadPlusTail_MatchesRoundTrip()
        => AssertIdentical(BuildFullHead(6), BuildTail(3, HeadFirstEpoch + 6));

    [Fact]
    public void TailCrossingQuantizationBoundary_MatchesRoundTrip()
    {
        // Tail values sitting on quantization midpoints (…x5 at the last kept digit) exercise the
        // away-from-zero rounding on the tail while the head copies verbatim.
        var tail = new List<StagingPoint>
        {
            new(0, 10.0000005, 20.0000005, 100.05, 3.005, 15.05, (long)(HeadFirstEpoch + 6), 1013.005),
            new(1, 10.0000015, 20.0000015, 100.15, 3.015, 25.15, (long)(HeadFirstEpoch + 7), 1013.015),
        };
        AssertIdentical(BuildFullHead(4), tail);
    }

    [Fact]
    public void DifferingQBias_TailFarFromHeadEpoch_MatchesRoundTrip()
    {
        // The tail epoch is a long way from the head's QBias base — the tail must rebase against the head's
        // QBias (large but in-range int32 offset), exactly as RequiredTime would over the concatenation.
        var head = BuildFullHead(4);
        var tail = BuildTail(3, HeadFirstEpoch + 500_000);
        AssertIdentical(head, tail);
    }

    [Fact]
    public void WholeNullOptionalChannels_StayWholeNull()
    {
        // Head has only lat/lng/time; staging supplies no altitude/etc. either → those channels stay null.
        var n = 4;
        var (time, lat, lng) = Ramps(n, HeadFirstEpoch);
        var head = DecodedTrackExtensions.FromDoubleChannels(time, lat, lng, null, null, null, null, null, null, null, null);
        var tail = new List<StagingPoint>
        {
            new(0, 11.0, 21.0, null, null, null, (long)(HeadFirstEpoch + n), null),
            new(1, 11.1, 21.1, null, null, null, (long)(HeadFirstEpoch + n + 1), null),
        };
        AssertIdentical(head, tail);
    }

    [Fact]
    public void HeadSensorChannels_NullOverTail_MatchesRoundTrip()
    {
        // Head carries hr/cadence/power/grade (staging never does) → present with NULL over the tail.
        int n = 5;
        var (time, lat, lng) = Ramps(n, HeadFirstEpoch);
        var hr = Fill(n, 140); var cad = Fill(n, 90); var pwr = Fill(n, 220); var grade = Fill(n, 2.5);
        var head = DecodedTrackExtensions.FromDoubleChannels(
            time, lat, lng, Fill(n, 100), Fill(n, 10), Fill(n, 5), hr, grade, cad, pwr, Fill(n, 1013));
        AssertIdentical(head, BuildTail(3, HeadFirstEpoch + n));
    }

    [Fact]
    public void PerPointNullsInHeadAndTail_MatchRoundTrip()
    {
        int n = 4;
        var (time, lat, lng) = Ramps(n, HeadFirstEpoch);
        var alt = Fill(n, 100);
        alt[2] = double.NaN;                      // a per-point null inside a present head channel
        var head = DecodedTrackExtensions.FromDoubleChannels(time, lat, lng, alt, null, null, null, null, null, null, null);
        var tail = new List<StagingPoint>
        {
            new(0, 11.0, 21.0, null, 3.0, null, (long)(HeadFirstEpoch + n), null),  // null altitude in the tail
            new(1, 11.1, 21.1, 205.0, 3.1, 10.0, null, null),                       // null timestamp in the tail
        };
        AssertIdentical(head, tail);
    }

    [Fact]
    public void HeadWithAllNullTime_RebasesOnTailEpoch()
    {
        int n = 3;
        var lat = Ramp(n, 10.0, 0.001); var lng = Ramp(n, 20.0, 0.001);
        var time = new[] { double.NaN, double.NaN, double.NaN };   // head has no timestamps
        var head = DecodedTrackExtensions.FromDoubleChannels(time, lat, lng, null, null, null, null, null, null, null, null);
        AssertIdentical(head, BuildTail(3, HeadFirstEpoch));
    }

    // ==== mutation-testing additions ====

    // L118 (`FirstPresentTailEpoch(staging) ?? 0` → `0`): a tail-only merge whose first epoch is beyond
    // int.MaxValue seconds MUST rebase QBias on that epoch, or the ~4e9 value overflows int32 and throws.
    // A pre-2038 epoch (< int.MaxValue) hides the drop — the far-future epoch is what forces it.
    [Fact]
    public void EmptyHead_FarFutureTail_MatchesRoundTrip()
        => AssertIdentical(null, BuildTail(3, 4_000_000_000.0));

    // L178 (`HasPresentSample` end `return false` → `true`): an all-NULL-time head must be treated as having
    // NO present time, so the merge rebases on the tail's first (far-future) epoch. The mutant claims the head
    // DOES have a present sample, keeps the head's QBias (0 for an all-null head), and the far-future tail
    // overflows int32.
    [Fact]
    public void HeadAllNullTime_FarFutureTail_RebasesOnTail()
    {
        int n = 3;
        var lat = Ramp(n, 10.0, 0.001); var lng = Ramp(n, 20.0, 0.001);
        var time = new[] { double.NaN, double.NaN, double.NaN };
        var head = DecodedTrackExtensions.FromDoubleChannels(time, lat, lng, null, null, null, null, null, null, null, null);
        AssertIdentical(head, BuildTail(3, 4_000_000_000.0));
    }

    // L175 (`if (raw is null) return false` condition → `true`, i.e. HasPresentSample always false): a head
    // WITH present timestamps must copy them verbatim across the merge. The mutant treats the head time as
    // absent and fills the head region with NULLs — losing the head's own timestamps.
    [Fact]
    public void HeadWithPresentTime_KeepsHeadTimestampsAcrossTail()
    {
        var head = BuildFullHead(5);
        var merged = ContentTrackReader.Merge(head, BuildTail(2, HeadFirstEpoch + 5));
        for (int i = 0; i < 5; i++)
            Assert.Equal(head.TimeEpochS[i], merged.TimeEpochS[i]);
    }

    // L138 (`double.IsNaN(v) ? NullQ : GuardQuantize(v, scale)` → `GuardQuantize(v, scale)` unconditionally):
    // a NaN in a required (lat/lng) tail sample must map to a per-point NULL, not be quantized to 0.
    [Fact]
    public void TailWithNaNRequiredValue_MapsToNull()
    {
        var head = BuildFullHead(3);
        var tail = new List<StagingPoint>
        {
            new(0, double.NaN, 21.0, 205.0, 3.0, 10.0, (long)(HeadFirstEpoch + 3), 1013.0),  // NaN latitude
            new(1, 11.1, 21.1, 206.0, 3.1, 11.0, (long)(HeadFirstEpoch + 4), 1013.1),
        };
        AssertIdentical(head, tail);
    }

    // L183 (loop `j < staging.Count` → `j <= staging.Count`, and `j++` → `j--`): FirstPresentTailEpoch is only
    // reached when NO tail sample carries a timestamp. A tail of all-null timestamps must return null (→ QBias
    // 0, all-NULL time), not walk off the end of the list.
    [Fact]
    public void EmptyHead_AllNullTimestampTail_IsAllNullTime()
    {
        var tail = new List<StagingPoint>
        {
            new(0, 30.0, 40.0, 200.0, 3.5, 0.0, null, 1013.0),
            new(1, 30.1, 40.1, 201.0, 3.5, 10.0, null, 1013.1),
            new(2, 30.2, 40.2, 202.0, 3.5, 20.0, null, 1013.2),
        };
        AssertIdentical(null, tail);
    }

    // ── Allocation measurement (the PERF-P1 before/after number) ───────────

    [Fact]
    public void Merge_AllocatesFarLessThanTheDoubleRoundTrip()
    {
        // A representative live poll: a large finalized head + a small live tail, read UNCACHED every poll.
        // GC.GetAllocatedBytesForCurrentThread is an exact per-thread counter (not a heap estimate), so this
        // is deterministic regardless of concurrent load. The static tail selectors are cached delegates, so
        // the new path allocates only its int[] channels — no double[] scratch, no head float work.
        var head = BuildFullHead(20_000);
        var tail = BuildTail(50, HeadFirstEpoch + 20_000);

        _ = ReferenceMerge(head, tail);            // warm up / JIT both paths + cache the delegates
        _ = ContentTrackReader.Merge(head, tail);

        const int iters = 20;
        long oldBytes = MeasureAlloc(() => { for (int i = 0; i < iters; i++) _ = ReferenceMerge(head, tail); }) / iters;
        long newBytes = MeasureAlloc(() => { for (int i = 0; i < iters; i++) _ = ContentTrackReader.Merge(head, tail); }) / iters;

        Console.WriteLine($"[PERF-P1] head=20000 tail=50: old={oldBytes:N0} B/merge  new={newBytes:N0} B/merge  " +
                          $"ratio={(double)newBytes / oldBytes:P0}");
        Assert.True(newBytes * 2 < oldBytes,
            $"expected the quantized-append merge to allocate <50% of the double round-trip; old={oldBytes} new={newBytes}");
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private static long MeasureAlloc(Action body)
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        body();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    private static void AssertIdentical(DecodedTrack? head, IReadOnlyList<StagingPoint> tail)
    {
        var expected = ReferenceMerge(head, tail);
        var actual = ContentTrackReader.Merge(head, tail);

        Assert.Equal(expected.Count, actual.Count);
        AssertChannel(expected.TimeEpochS, actual.TimeEpochS, expected.Count, "time");
        AssertChannel(expected.Lat, actual.Lat, expected.Count, "lat");
        AssertChannel(expected.Lng, actual.Lng, expected.Count, "lng");
        AssertChannel(expected.Altitude, actual.Altitude, expected.Count, "altitude");
        AssertChannel(expected.Distance, actual.Distance, expected.Count, "distance");
        AssertChannel(expected.Speed, actual.Speed, expected.Count, "speed");
        AssertChannel(expected.HeartRate, actual.HeartRate, expected.Count, "heartRate");
        AssertChannel(expected.GradeSmooth, actual.GradeSmooth, expected.Count, "gradeSmooth");
        AssertChannel(expected.Cadence, actual.Cadence, expected.Count, "cadence");
        AssertChannel(expected.Power, actual.Power, expected.Count, "power");
        AssertChannel(expected.Pressure, actual.Pressure, expected.Count, "pressure");

        // Strongest cross-check: the two decodes must encode to byte-identical v1 blobs.
        Assert.Equal(TrackBlobCodec.Encode(expected), TrackBlobCodec.Encode(actual));
    }

    private static void AssertChannel(TrackChannel expected, TrackChannel actual, int count, string name)
    {
        Assert.True(expected.IsNull == actual.IsNull, $"{name}: IsNull mismatch (exp {expected.IsNull})");
        if (expected.IsNull) return;
        for (int i = 0; i < count; i++)
        {
            double e = expected[i], a = actual[i];
            if (double.IsNaN(e)) Assert.True(double.IsNaN(a), $"{name}[{i}]: expected NaN, got {a}");
            else Assert.True(e == a, $"{name}[{i}]: expected {e}, got {a}");
        }
    }

    private static DecodedTrack BuildFullHead(int n)
    {
        var (time, lat, lng) = Ramps(n, HeadFirstEpoch);
        return DecodedTrackExtensions.FromDoubleChannels(
            time, lat, lng,
            Ramp(n, 100.0, 1.0), Ramp(n, 0.0, 10.0), Fill(n, 3.5),
            Fill(n, 140), Ramp(n, 1.0, 0.1), Fill(n, 90), Fill(n, 220), Ramp(n, 1013.0, 0.01));
    }

    private static List<StagingPoint> BuildTail(int count, double startEpoch)
    {
        var list = new List<StagingPoint>(count);
        for (int i = 0; i < count; i++)
            list.Add(new StagingPoint(
                i, 30.0 + i * 0.001, 40.0 + i * 0.001,
                200.0 + i, 3.5, i * 10.0, (long)(startEpoch + i), 1013.0 + i * 0.01));
        return list;
    }

    private static (double[] time, double[] lat, double[] lng) Ramps(int n, double startEpoch)
        => (Ramp(n, startEpoch, 1.0), Ramp(n, 10.0, 0.001), Ramp(n, 20.0, 0.001));

    private static double[] Ramp(int n, double start, double step)
    {
        var a = new double[n];
        for (int i = 0; i < n; i++) a[i] = start + i * step;
        return a;
    }

    private static double[] Fill(int n, double v)
    {
        var a = new double[n];
        Array.Fill(a, v);
        return a;
    }

    /// <summary>The OLD merge: dequantize the whole head + tail into double[] channels, then re-quantize the
    /// lot through FromDoubleChannels. Kept here as the value-identity oracle for the new fast path.</summary>
    private static DecodedTrack ReferenceMerge(DecodedTrack? headPart, IReadOnlyList<StagingPoint> staging)
    {
        int headN = headPart?.Count ?? 0;
        int n = headN + staging.Count;

        var lat = new double[n]; var lng = new double[n]; var time = new double[n];
        var altitude = new double[n]; var distance = new double[n]; var speed = new double[n];
        var heartRate = new double[n]; var gradeSmooth = new double[n]; var cadence = new double[n];
        var power = new double[n]; var pressure = new double[n];

        for (int i = 0; i < headN; i++)
        {
            lat[i] = headPart!.Lat[i];
            lng[i] = headPart.Lng[i];
            time[i] = headPart.TimeEpochS[i];
            altitude[i] = At(headPart.Altitude, i);
            distance[i] = At(headPart.Distance, i);
            speed[i] = At(headPart.Speed, i);
            heartRate[i] = At(headPart.HeartRate, i);
            gradeSmooth[i] = At(headPart.GradeSmooth, i);
            cadence[i] = At(headPart.Cadence, i);
            power[i] = At(headPart.Power, i);
            pressure[i] = At(headPart.Pressure, i);
        }
        for (int j = 0; j < staging.Count; j++)
        {
            int i = headN + j;
            var s = staging[j];
            lat[i] = s.Lat; lng[i] = s.Lng; time[i] = s.TsEpoch ?? double.NaN;
            altitude[i] = s.Altitude ?? double.NaN; distance[i] = s.Distance ?? double.NaN; speed[i] = s.Speed ?? double.NaN;
            heartRate[i] = double.NaN; gradeSmooth[i] = double.NaN; cadence[i] = double.NaN; power[i] = double.NaN;
            pressure[i] = s.Pressure ?? double.NaN;
        }
        return DecodedTrackExtensions.FromDoubleChannels(
            time, lat, lng, altitude, distance, speed, heartRate, gradeSmooth, cadence, power, pressure);
    }

    private static double At(TrackChannel c, int i) => c.IsNull ? double.NaN : c[i];
}
