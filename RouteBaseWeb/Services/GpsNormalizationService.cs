using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Elevation;
using RouteBaseWeb.Services.Interfaces;

namespace RouteBaseWeb.Services;

/// <inheritdoc />
public class GpsNormalizationService : IGpsNormalizationService
{
    // Altitude noise filter: ignore deltas smaller than this to avoid GPS jitter inflating ascent/descent.
    private const double AltitudeNoiseFilterMeters = 2.0;

    // Speed threshold (m/s) below which a point is considered "not moving".
    private const double MovingSpeedThreshold = 0.5;

    // EMA factor used ONLY as the reference for spike rejection below (not for the output smoothing,
    // which is now the curvature smoother in ElevationProfileSmoother). Lower = steadier reference.
    private const double AltitudeSmoothingAlpha = 0.15;

    // If a GPS altitude point deviates more than this from the running EMA, treat it as an outlier
    // and clamp it to the EMA value. Eliminates isolated GPS altitude spikes (e.g. 50 m jumps).
    private const double AltitudeSpikeThresholdMeters = 30.0;

    // A deviation this many consecutive points agree with is a real STEP (DEM cliff, tunnel exit), not a
    // spike — accept it and re-anchor. Without this, one sustained step got clamped point after point.
    private const int AltitudeStepConfirmPoints = 3;

    // Segment break: consecutive points that are not a continuous leg — a recording gap (phone paused,
    // fix lost) or a merge seam. Measured on the failing ride: normal legs run to 13 m / 10 s at p99.9,
    // while every real break was ≥161 m and ≥102 s, so these gates split the breaks and nothing else.
    private const double SegmentBreakSeconds = 60.0;
    private const double SegmentBreakMeters = 150.0;

    // Lateral outlier gate: a point implying more than this ground speed (m/s) from the previous kept
    // point is a bad fix, not real movement. 40 m/s = 144 km/h — above any run/ride, below the hundreds
    // of m/s a positional spike implies. GPS teleports are gross, so a generous ceiling avoids false hits.
    private const double MaxPlausibleSpeedMs = 40.0;

    // If this many points in a row look like outliers, the anchor itself was the bad fix — re-anchor to
    // the current point instead of deleting the rest of the track.
    private const int MaxConsecutiveDrops = 3;

    // Trailing-dwell trim. An auto-recording runs until the phone's idle watchdog fires (8 min of no
    // movement), so it always ends with the rider parked at home/work while the GPS jitters in place.
    // Speed cannot see that: measured over 13 real auto-recordings, parked jitter crosses the 0.5 m/s
    // moving gate continuously, so a speed-based trim removed 0-16 s of a 2.5-19 min tail. Net progress
    // can — jitter piles up path distance while going nowhere. Trimmed durations move by only ~0.5 min
    // across W 120-180 s / R 50-100 m, i.e. these sit in a gap between regimes, not on a cliff.
    private const double DwellWindowSeconds = 120.0;   // how far ahead to look for real progress
    private const double DwellRadiusMeters = 60.0;     // less net progress than this in that window = parked
    // Below this a stop is a pause (traffic light, waiting to cross), not an arrival. 120 rather than 90
    // because a real run that ended walking home lost its last 1.4 min at 90 — the walk decays into a
    // stop over about a minute, and a floor under 2 min starts eating that decay.
    private const double MinDwellSeconds = 120.0;

    // The dwell scan answers "is there a park at the end", but it flags a point as dwelling as soon as
    // the rider is within DwellRadiusMeters of where they will settle — up to a minute of the final
    // approach at riding speed. This is the jitter scale (p99 step on real parked tails: 2-29 m), used
    // to walk the cut forward to the point that actually reaches the resting spot.
    private const double ParkedRadiusMeters = 25.0;

    /// <inheritdoc />
    public void RejectPositionOutliers(IList<GpsData> points)
    {
        if (points.Count < 3) return;   // too few to judge; a 2-point track has no "previous kept" context

        // Allocated lazily on the first drop only — a clean track (the overwhelming common case) makes
        // one O(n) pass and zero heap allocation, leaving the caller's list untouched.
        List<GpsData>? kept = null;
        var last = points[0];
        int consecutiveDrops = 0;

        for (int i = 1; i < points.Count; i++)
        {
            var pt = points[i];
            if (consecutiveDrops < MaxConsecutiveDrops && IsTeleport(last, pt))
            {
                // First outlier: seed the survivor list with everything accepted so far (points[0..i-1]).
                if (kept is null)
                {
                    kept = new List<GpsData>(points.Count);
                    for (int j = 0; j < i; j++) kept.Add(points[j]);
                }
                consecutiveDrops++;   // drop pt, keep the same anchor
                continue;
            }
            kept?.Add(pt);
            last = pt;
            consecutiveDrops = 0;
        }

        if (kept is null) return;   // nothing dropped — original list untouched
        points.Clear();
        foreach (var pt in kept) points.Add(pt);
    }

    // A point is a teleport if the implied speed from the anchor exceeds the plausible ceiling.
    // Missing coordinates or non-increasing timestamps can't be judged → treated as valid (kept).
    private static bool IsTeleport(GpsData from, GpsData to)
    {
        if (!from.GpsLat.HasValue || !from.GpsLong.HasValue ||
            !to.GpsLat.HasValue   || !to.GpsLong.HasValue ||
            !from.DateTimeUtc.HasValue || !to.DateTimeUtc.HasValue)
            return false;

        double dt = (to.DateTimeUtc.Value - from.DateTimeUtc.Value).TotalSeconds;
        if (dt <= 0) return false;

        double dist = GeoUtils.HaversineDistance(
            from.GpsLat.Value, from.GpsLong.Value, to.GpsLat.Value, to.GpsLong.Value);
        return dist / dt > MaxPlausibleSpeedMs;
    }

    /// <inheritdoc />
    public void TrimTrailingDwell(IList<GpsData> points)
    {
        int arrival = FindTrailingDwellStart(points);
        if (arrival < 0) return;   // still moving at the end, or never moved — nothing to trim

        for (int i = points.Count - 1; i > arrival; i--)
            points.RemoveAt(i);
    }

    /// <summary>
    /// Index of the arrival point: the first point of the run of dwelling that reaches the end of the
    /// track. Returns -1 when there is nothing to trim. Only the run touching the end counts, which is
    /// what makes a mid-ride café or traffic stop safe — the riding that follows it breaks the run.
    /// </summary>
    private static int FindTrailingDwellStart(IList<GpsData> points)
    {
        if (points.Count < 3) return -1;

        // A point without a fix or a timestamp says nothing about movement, so it must neither start
        // nor break a dwell — compact those out and judge the rest.
        var origin = new List<int>(points.Count);
        var lat = new List<double>(points.Count);
        var lon = new List<double>(points.Count);
        var sec = new List<double>(points.Count);
        DateTime? epoch = null;
        for (int i = 0; i < points.Count; i++)
        {
            var pt = points[i];
            if (!pt.GpsLat.HasValue || !pt.GpsLong.HasValue || !pt.DateTimeUtc.HasValue) continue;
            epoch ??= pt.DateTimeUtc.Value;
            origin.Add(i);
            lat.Add(pt.GpsLat.Value);
            lon.Add(pt.GpsLong.Value);
            sec.Add((pt.DateTimeUtc.Value - epoch.Value).TotalSeconds);
        }

        int n = origin.Count;
        if (n < 3) return -1;

        // Dwelling at i = the rider is still within DwellRadiusMeters of point i one window later.
        // Both pointers only advance, so the look-ahead costs one pass.
        var dwelling = new bool[n];
        bool lastVerdict = false;
        int j = 0;
        for (int i = 0; i < n; i++)
        {
            if (j < i) j = i;
            while (j + 1 < n && sec[j + 1] <= sec[i] + DwellWindowSeconds) j++;

            // The last window is truncated by the end of the track. Too little data to call it either
            // way, so inherit the last real verdict rather than read a few seconds as "parked".
            if (i > 0 && sec[j] - sec[i] < DwellWindowSeconds / 2)
            {
                dwelling[i] = lastVerdict;
                continue;
            }

            dwelling[i] = GeoUtils.HaversineDistance(lat[i], lon[i], lat[j], lon[j]) < DwellRadiusMeters;
            lastVerdict = dwelling[i];
        }

        if (!dwelling[n - 1]) return -1;   // last real movement runs to the end — a genuine finish

        int start = n - 1;
        while (start > 0 && dwelling[start - 1]) start--;

        if (start == 0) return -1;                                    // never moved — don't trim to nothing
        if (sec[n - 1] - sec[start] < MinDwellSeconds) return -1;     // a pause, not an arrival

        // `start` is only where progress stopped counting; the rider is still closing the last stretch.
        // Walk forward to the first fix that actually reaches the resting spot. Scanning forward (rather
        // than back from the end) is what keeps a late jitter excursion from dragging the cut out again.
        double restLat = 0, restLon = 0;
        int restCount = 0;
        for (int i = n - 1; i >= start && sec[n - 1] - sec[i] <= MinDwellSeconds; i--)
        {
            restLat += lat[i];
            restLon += lon[i];
            restCount++;
        }
        restLat /= restCount;
        restLon /= restCount;

        int arrival = start;
        while (arrival < n - 1 &&
               GeoUtils.HaversineDistance(lat[arrival], lon[arrival], restLat, restLon) > ParkedRadiusMeters)
            arrival++;

        return origin[arrival];
    }

    /// <summary>
    /// True when consecutive points are not one continuous leg — a recording gap or a merge seam. The
    /// elevation across such a break is a real STEP (you were somewhere else), so neither the spike
    /// reference nor the arc-length smoother may carry over it, and it contributes no ascent.
    /// </summary>
    private static bool IsSegmentBreak(GpsData prev, GpsData cur)
    {
        if (prev.DateTimeUtc.HasValue && cur.DateTimeUtc.HasValue &&
            (cur.DateTimeUtc.Value - prev.DateTimeUtc.Value).TotalSeconds > SegmentBreakSeconds)
            return true;

        if (prev.GpsLat.HasValue && prev.GpsLong.HasValue && cur.GpsLat.HasValue && cur.GpsLong.HasValue &&
            GeoUtils.HaversineDistance(
                prev.GpsLat.Value, prev.GpsLong.Value, cur.GpsLat.Value, cur.GpsLong.Value) > SegmentBreakMeters)
            return true;

        return false;
    }

    /// <inheritdoc />
    public void SmoothAltitudes(IList<GpsData> points)
    {
        if (points.Count == 0)
            return;

        // Fuse-or-fall-back is decided ONCE, over the WHOLE track. A barometer that dies mid-ride must
        // fall back everywhere: switching formula halfway leaves a visible step in the profile. This is
        // deliberately NOT the same question the kernel asks per segment (can I solve THIS stretch).
        var trackPressure = new double[points.Count];
        for (int i = 0; i < points.Count; i++)
            trackPressure[i] = points[i].Pressure ?? double.NaN;
        bool fusePressure = ElevationFusion.HasUsablePressure(trackPressure);

        // Smooth each continuous segment on its own. Bridging a break used to destroy the rest of the
        // track: the seam's legitimate elevation step read as a spike, and clamping fed the clamped value
        // back into the EMA (ema = a·ema + (1−a)·ema = ema), freezing the reference — every later point
        // then clamped to that frozen value, flattening the profile to the end of the ride.
        //
        // Splitting here is also what stops the BAROMETER bridging a pause: pressure keeps changing
        // while you drive to the trailhead, and a fused profile interpolated through one would bank the
        // whole altitude change as climbing. The fusion kernel needs no gap handling of its own.
        int start = 0;
        for (int i = 1; i <= points.Count; i++)
        {
            if (i < points.Count && !IsSegmentBreak(points[i - 1], points[i]))
                continue;
            SmoothSegment(points, start, i, fusePressure);
            start = i;
        }
    }

    /// <summary>
    /// One point's smoothing inputs, captured AT the null guard so the nullable channels are never
    /// re-dereferenced behind a `!` later (H2). Named rather than a tuple because five of the six
    /// members are doubles, and positional construction of five same-typed values is a transposition
    /// waiting to happen — the surface <c>BackendConventionTests</c> pins.
    /// </summary>
    private readonly record struct SmoothInput(
        int Index,
        double Lat,
        double Lng,
        double Alt,
        double Hpa,
        double Seconds);

    /// <summary>Spike rejection + curvature smoothing over one continuous run of points, [start, end).</summary>
    private static void SmoothSegment(IList<GpsData> points, int start, int end, bool fusePressure)
    {
        // 1) Spike rejection — clamp an altitude that deviates from the running EMA reference by more than
        //    AltitudeSpikeThresholdMeters AND that its successors do not confirm. A deviation the next few
        //    points agree with is real terrain, so re-anchor to it instead of fighting it. Removes isolated
        //    GPS-altitude spikes; a no-op on the smooth DEM profile.
        double? ema = null;
        for (int i = start; i < end; i++)
        {
            var pt = points[i];
            if (!pt.Altitude.HasValue)
                continue;

            double raw = pt.Altitude.Value;
            if (ema is null) { ema = raw; continue; }

            if (Math.Abs(raw - ema.Value) > AltitudeSpikeThresholdMeters)
            {
                if (IsConfirmedStep(points, i, end, raw))
                {
                    ema = raw;          // real step — re-anchor, keep the point
                    continue;
                }
                pt.Altitude = ema.Value;   // isolated spike — clamp the point
            }

            // Always advance on the RAW value, never on the clamp: feeding a clamped value back leaves the
            // EMA exactly where it was, and a frozen reference can never recover.
            ema = AltitudeSmoothingAlpha * raw + (1 - AltitudeSmoothingAlpha) * ema.Value;
        }

        // 2) Curvature smoother (Whittaker-2 at the GAIN length, 70 m; dead-flat tracks auto-switch to
        //    ~200 m) on the altitude-vs-distance profile — the validated replacement for the EMA. The
        //    length is passed explicitly: the smoother's default (105 m) belongs to the race grade path,
        //    which wants a smooth cost curve, not accurate ascent. Requires coordinates; a segment with
        //    none is left de-spiked.
        // Values captured AT the guard (H2): the old index list re-dereferenced the nullable
        // channels later behind `!` — same invariant, now carried by the tuple's types.
        //
        // ⚠ Pressure and the timestamp are read INSIDE the pattern's body, never added to the pattern
        // itself: the pattern is the filter deciding sel.Count, so requiring a pressure there would drop
        // every non-barometric point out of smoothing entirely — a silent regression on every track.
        DateTime? epoch = null;
        for (int i = start; i < end && epoch is null; i++)
            epoch = points[i].DateTimeUtc;

        var sel = new List<SmoothInput>(end - start);
        for (int i = start; i < end; i++)
            if (points[i] is { Altitude: double al, GpsLat: double la, GpsLong: double lo } pt)
                sel.Add(new SmoothInput(
                    Index: i, Lat: la, Lng: lo, Alt: al,
                    Hpa: pt.Pressure ?? double.NaN,
                    Seconds: pt.DateTimeUtc is { } stamp && epoch is { } zero
                        ? (stamp - zero).TotalSeconds
                        : double.NaN));
        if (sel.Count < 3)
            return;

        var arc = new double[sel.Count];
        var alt = new double[sel.Count];
        var pressure = new double[sel.Count];
        var seconds = new double[sel.Count];
        alt[0] = sel[0].Alt;
        pressure[0] = sel[0].Hpa;
        seconds[0] = sel[0].Seconds;
        for (int k = 1; k < sel.Count; k++)
        {
            arc[k] = arc[k - 1] + GeoUtils.HaversineDistance(
                sel[k - 1].Lat, sel[k - 1].Lng, sel[k].Lat, sel[k].Lng);
            alt[k] = sel[k].Alt;
            pressure[k] = sel[k].Hpa;
            seconds[k] = sel[k].Seconds;
        }

        // Delegation, not equivalence: with no usable barometer this calls the SAME function it always
        // did, so a barometer-less user cannot possibly get a different answer than today. Setting β=0
        // with a constant λ was once ASSUMED to reproduce this and does not — production's flat regime
        // fires on 100 of 204 real activities, where the weighted path drifted up to 7.3 m of elevation
        // and 20.4 m of gain. Only a real-activity sweep could see it.
        var smoothed = fusePressure
            ? ElevationFusion.SmoothByArcLength(arc, alt, pressure, seconds,
                fallbackLengthMeters: ElevationProfileSmoother.GainSmoothLengthMeters)
            : ElevationProfileSmoother.SmoothByArcLength(
                arc, alt, ElevationProfileSmoother.GainSmoothLengthMeters);
        for (int k = 0; k < sel.Count; k++)
            points[sel[k].Index].Altitude = smoothed[k];
    }

    // A step is a deviation the next AltitudeStepConfirmPoints altitude-bearing points stay close to.
    // Too few followers left in the segment (its tail) → unconfirmable → treated as a spike.
    private static bool IsConfirmedStep(IList<GpsData> points, int i, int end, double raw)
    {
        int confirmed = 0;
        for (int j = i + 1; j < end && confirmed < AltitudeStepConfirmPoints; j++)
        {
            if (points[j].Altitude is not double follower)
                continue;
            if (Math.Abs(follower - raw) > AltitudeSpikeThresholdMeters)
                return false;
            confirmed++;
        }
        return confirmed == AltitudeStepConfirmPoints;
    }

    /// <inheritdoc />
    public GpsNormalizationStats ComputeStats(IList<GpsData> points)
    {
        if (points.Count == 0)
            return new GpsNormalizationStats();

        double totalDistance = 0;
        double totalAscent = 0;
        double totalDescent = 0;
        double timeMovingSeconds = 0;
        double speedMax = 0;
        double? prevAlt = null;
        double altAccumulator = 0;

        GpsData? prev = null;          // immediate predecessor — for moving-time (timestamp-adjacent pairs)
        (double Lat, double Lng)? prevCoord = null;     // previous coordinate-bearing point — for distance (skips null coords)
        GpsData? firstCoord = null;    // first/last point WITH coords → start/end pins (not raw endpoints)
        GpsData? lastCoord = null;
        var firstPoint = points[0];
        var lastPoint = points[^1];

        foreach (var pt in points)
        {
            // A gap/seam is not a climb: you were somewhere else, not on the hill. Crossing one must not
            // bank its elevation step as ascent (a merge seam banked +195 m of climbing you never rode).
            bool isBreak = prev is not null && IsSegmentBreak(prev, pt);

            // Distance: accumulate only between coordinate-bearing points. A null-coord point (FIT records
            // with no position) would otherwise inject two legs to/from (0,0) — a ~13,000 km spike.
            if (pt.GpsLat is double curLat && pt.GpsLong is double curLng)
            {
                // The previous coordinate is captured AS a value pair (H2) — no nullable re-deref.
                if (prevCoord is { } pc)
                    totalDistance += GeoUtils.HaversineDistance(pc.Lat, pc.Lng, curLat, curLng);
                prevCoord = (curLat, curLng);
                firstCoord ??= pt;
                lastCoord = pt;
            }

            if (prev is not null)
            {
                // Moving time — but not across a break (SVC-01). A pause, a merge seam or a multi-day stitch
                // is not time spent moving: banking the full inter-point gap would inflate TimeMoving (and
                // every pace derived from it) by minutes-to-days. Mirrors the ascent gate below, which already
                // drops the step across a break — the two must agree on what "crossing a seam" means.
                if (!isBreak && pt.Speed is > MovingSpeedThreshold && prev.DateTimeUtc.HasValue && pt.DateTimeUtc.HasValue)
                    timeMovingSeconds += (pt.DateTimeUtc.Value - prev.DateTimeUtc.Value).TotalSeconds;
            }

            // Speed max
            if (pt.Speed is double spd && spd > speedMax)
                speedMax = spd;

            // Ascent / descent with noise filter. A break restarts the run: the step across it is dropped,
            // as is any partial accumulation that had not yet cleared the noise filter.
            if (isBreak)
            {
                altAccumulator = 0;
                prevAlt = null;
            }

            if (pt.Altitude.HasValue)
            {
                if (prevAlt.HasValue)
                {
                    double delta = pt.Altitude.Value - prevAlt.Value;
                    altAccumulator += delta;

                    if (Math.Abs(altAccumulator) >= AltitudeNoiseFilterMeters)
                    {
                        if (altAccumulator > 0) totalAscent += altAccumulator;
                        else totalDescent += -altAccumulator;
                        altAccumulator = 0;
                    }
                }
                prevAlt = pt.Altitude.Value;
            }

            prev = pt;
        }

        double timeTotalSeconds = 0;
        if (firstPoint.DateTimeUtc.HasValue && lastPoint.DateTimeUtc.HasValue)
            timeTotalSeconds = (lastPoint.DateTimeUtc.Value - firstPoint.DateTimeUtc.Value).TotalSeconds;

        double speedAvg = timeTotalSeconds > 0 ? totalDistance / timeTotalSeconds : 0;

        return new GpsNormalizationStats
        {
            StartUtc = firstPoint.DateTimeUtc,   // wall-clock start is the raw first timestamp (unchanged)
            StartLat = firstCoord?.GpsLat,       // start/end pins come from the first/last actual fix
            StartLng = firstCoord?.GpsLong,
            EndLat = lastCoord?.GpsLat,
            EndLng = lastCoord?.GpsLong,
            TotalDistanceMeters = totalDistance,
            TotalAscentMeters = totalAscent,
            TotalDescentMeters = totalDescent,
            TimeTotalSeconds = timeTotalSeconds,
            TimeMovingSeconds = timeMovingSeconds,
            SpeedAvgMs = speedAvg,
            SpeedMaxMs = speedMax,
        };
    }
}
