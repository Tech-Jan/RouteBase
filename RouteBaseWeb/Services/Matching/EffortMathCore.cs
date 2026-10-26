using RouteBaseWeb.Services.Tracks;

namespace RouteBaseWeb.Services.Matching;

/// <summary>
/// Pure, I/O-free port of the SQL post-pipeline effort math — the pair-coherence offset search
/// (SegmentService.Offsets.cs) and the per-effort aggregates (SegmentService.Aggregates.cs).
/// One decoded track + one effort window per call; no DbContext, no temp tables, no ITrackReader.
///
/// PARITY (invariant 2, bug-for-bug): every branch reproduces the S8/S9 catalog verbatim —
/// SQLite Round5 band math (via <see cref="SqliteCompat.Round5"/>), the strict `exit &gt; entry`
/// pairing, the 9.0e15 COALESCE score, the 10% guard's NULL-cum bypass and NULL-segment-distance
/// exclusion, the deterministic tie-break, the guessed-id fallback with raw pin offsets, the
/// julianday effort_time (via <see cref="SqliteCompat.EffortTimeSeconds"/>), positive-only
/// elevation deltas in GPS_ID order, HR-positive-only / power-present cadence quirks.
///
/// The quality-guard DELETEs (S8.13) are NOT here — the adapter runs the two raw-SQL statements.
/// </summary>
public static class EffortMathCore
{
    // S8.5: band tolerance literal, added to the window minimum exactly as the SQL expression
    // `d <= min_d + 0.0002` (never pre-subtracted from d).
    private const double BandTol = 0.0002;

    // S8.8: 10% guard fraction and S8.7 worst-case COALESCE score.
    private const double GuardFraction = 0.1;
    private const double WorstScore = 9.0e15;

    /// <summary>
    /// Computes the calculated entry/exit offsets for one own effort (S8). Returns the winning
    /// pair when one survives the 10% guard, else the guessed-id fallback with raw pin offsets.
    /// </summary>
    public static OffsetResult ComputeOffsets(
        EffortWindow w, DecodedTrack track,
        double pinStartLat, double pinStartLng, double pinEndLat, double pinEndLng,
        double? segmentDistance)
    {
        var (lo, hi) = track.OrdinalRange(w.EntryGuessed, w.ExitGuessed);
        int n = hi - lo + 1;

        if (n > 0)
        {
            // Pass 1: Round5 pin distances for every window point + per-band minima (NaN coords
            // → NaN d → excluded from the MIN and from the band, matching SQL's NULL handling).
            var dStart = new double[n];
            var dEnd   = new double[n];
            double minDStart = double.PositiveInfinity, minDEnd = double.PositiveInfinity;
            for (int k = 0; k < n; k++)
            {
                int i = lo + k;
                double lat = track.Lat[i], lng = track.Lng[i];
                double ds = SqliteCompat.Round5(Math.Abs(pinStartLat - lat) + Math.Abs(pinStartLng - lng));
                double de = SqliteCompat.Round5(Math.Abs(pinEndLat   - lat) + Math.Abs(pinEndLng   - lng));
                dStart[k] = ds;
                dEnd[k]   = de;
                if (!double.IsNaN(ds) && ds < minDStart) minDStart = ds;
                if (!double.IsNaN(de) && de < minDEnd)   minDEnd   = de;
            }

            // Pass 2: enumerate start-band × end-band pairs (both ascending GPS_ID, since ordinals
            // ascend), keep the winner by (score, offsetSum, entryGpsId, exitGpsId).
            double bestScore = 0, bestSum = 0;
            int bestEntryGps = 0, bestExitGps = 0;
            double bestEntryOff = 0, bestExitOff = 0;
            bool haveWinner = false;

            // SD-03: precompute the end-band ordinals ONCE (ascending b) instead of re-scanning all n
            // window points per start-band member — O(n + |A|·n) → O(n + |A|·|B|), output-identical
            // (same pairs, same ascending enumeration, same tie-break). Loop/multi-pass efforts keep
            // n in the 10^4–10^5 range; this bounds the pathological tail. The NaN-propagation shape
            // of `!(de <= …)` is copied verbatim so all-NaN ends produce an empty band, same as before.
            var endBand = new List<int>(8);
            var endBandCum = new List<double>(8);   // Cum(track, lo+b) is invariant across the outer a-loop
            for (int b = 0; b < n; b++)
            {
                double de = dEnd[b];
                if (double.IsNaN(de) || !(de <= minDEnd + BandTol)) continue;        // S8.5 end band
                endBand.Add(b);
                endBandCum.Add(Cum(track, lo + b));
            }

            for (int a = 0; a < n; a++)
            {
                double ds = dStart[a];
                if (double.IsNaN(ds) || !(ds <= minDStart + BandTol)) continue;     // S8.5 start band
                int entryGps = lo + a;
                double cumS = Cum(track, lo + a);

                for (int e = 0; e < endBand.Count; e++)
                {
                    int b = endBand[e];
                    int exitGps = lo + b;
                    if (exitGps <= entryGps) continue;                              // S8.6 strict >
                    double de = dEnd[b];
                    double cumE = endBandCum[e];

                    // S8.8 10% guard: NULL cum on either side bypasses; NULL segment_distance makes
                    // the `>=` term unknown so only NULL-cum pairs can be admitted.
                    bool admit = double.IsNaN(cumS) || double.IsNaN(cumE)
                              || (segmentDistance.HasValue && (cumE - cumS) >= segmentDistance.Value * GuardFraction);
                    if (!admit) continue;

                    // S8.7 score: COALESCE(ABS((exit−entry)−segDist), 9e15) — any NULL operand → 9e15.
                    double score = (double.IsNaN(cumS) || double.IsNaN(cumE) || !segmentDistance.HasValue)
                        ? WorstScore
                        : Math.Abs((cumE - cumS) - segmentDistance.Value);
                    double sum = ds + de;

                    if (!haveWinner || IsBetter(score, sum, entryGps, exitGps,
                                                bestScore, bestSum, bestEntryGps, bestExitGps))
                    {
                        haveWinner   = true;
                        bestScore    = score;
                        bestSum      = sum;
                        bestEntryGps = entryGps;
                        bestExitGps  = exitGps;
                        bestEntryOff = ds;
                        bestExitOff  = de;
                    }
                }
            }

            if (haveWinner)                                                          // S8.10 atomic pair
                return new OffsetResult(w.GpsInSegmentId, bestEntryGps, bestEntryOff, bestExitGps, bestExitOff);
        }

        // S8.11/S8.12 fallback: calc = guessed id; offset = Round5 pin distance at the exact guessed
        // GPS_ID (missing id or NaN coords → NULL offset). Entry and exit evaluated independently.
        double? offsetEntry = PinOffsetAtGpsId(track, w.EntryGuessed, pinStartLat, pinStartLng);
        double? offsetExit  = PinOffsetAtGpsId(track, w.ExitGuessed,  pinEndLat,   pinEndLng);
        return new OffsetResult(w.GpsInSegmentId, w.EntryGuessed, offsetEntry, w.ExitGuessed, offsetExit);
    }

    /// <summary>
    /// Computes the 7 effort aggregates over one normalized window (S9). Returns null when the
    /// window contains no GPS points (S9.10 → no write). Caller must pass Lo ≤ Hi (S9.1).
    /// </summary>
    public static AggregateResult? ComputeAggregates(AggregateWindow w, DecodedTrack track)
    {
        if (w.Lo > w.Hi)
            throw new ArgumentException($"AggregateWindow requires Lo <= Hi (got {w.Lo} > {w.Hi})", nameof(w));

        var (lo, hi) = track.OrdinalRange(w.Lo, w.Hi);
        if (lo > hi) return null;                                                    // S9.10 no points

        double distMin = double.PositiveInfinity, distMax = double.NegativeInfinity;
        bool anyDist = false;
        double elev = 0, prevAlt = double.NaN;
        bool havePrev = false;
        double tMin = double.PositiveInfinity, tMax = double.NegativeInfinity;
        bool anyTime = false;
        double speedSum = 0; int speedCount = 0;
        double hrSum = 0;    int hrCount = 0;
        double powerSum = 0; int powerCount = 0;
        double cadenceOverPowerSum = 0;

        for (int i = lo; i <= hi; i++)
        {
            // S9.3 distance: MAX − MIN over non-NaN.
            double dist = Channel(track.Distance, i);
            if (!double.IsNaN(dist))
            {
                if (dist < distMin) distMin = dist;
                if (dist > distMax) distMax = dist;
                anyDist = true;
            }

            // S9.4 elevation: sum of positive consecutive deltas in GPS_ID order; first point and
            // any NaN endpoint contribute 0 (LAG semantics). prevAlt tracks the immediate predecessor.
            double alt = Channel(track.Altitude, i);
            if (havePrev && !double.IsNaN(alt) && !double.IsNaN(prevAlt))
            {
                double delta = alt - prevAlt;
                if (delta > 0) elev += delta;
            }
            prevAlt = alt;
            havePrev = true;

            // S9.5 time: MIN/MAX over non-NaN TimeEpochS (NOT first/last — some tracks are
            // non-monotonic under GPS_ID order).
            double t = track.TimeEpochS[i];
            if (!double.IsNaN(t))
            {
                if (t < tMin) tMin = t;
                if (t > tMax) tMax = t;
                anyTime = true;
            }

            // S9.6 speed: AVG over non-NaN.
            double sp = Channel(track.Speed, i);
            if (!double.IsNaN(sp)) { speedSum += sp; speedCount++; }

            // S9.7 hr: AVG over strictly-positive samples only.
            double hr = Channel(track.HeartRate, i);
            if (!double.IsNaN(hr) && hr > 0) { hrSum += hr; hrCount++; }

            // S9.8/S9.9 power + cadence-given-power: cadence averaged over power-present points,
            // NULL cadence counted as 0 there (COALESCE); divisor is COUNT(power).
            double pw = Channel(track.Power, i);
            if (!double.IsNaN(pw))
            {
                powerSum += pw;
                powerCount++;
                double cad = Channel(track.Cadence, i);
                cadenceOverPowerSum += double.IsNaN(cad) ? 0 : cad;
            }
        }

        double dst        = anyDist ? distMax - distMin : 0;                                     // S9.3 COALESCE 0
        double timeS      = anyTime ? SqliteCompat.EffortTimeSeconds(tMin, tMax) : 0;            // S9.5 COALESCE 0
        double speedAvg   = speedCount > 0 ? speedSum / speedCount : 0;                          // S9.6 COALESCE 0
        double? hrAvg     = hrCount > 0 ? hrSum / hrCount : null;                                // S9.7 null when none
        double? powerAvg  = powerCount > 0 ? powerSum / powerCount : null;                       // S9.8 null when none
        double? cadence   = powerCount > 0 ? cadenceOverPowerSum / powerCount : null;            // S9.9 null without power

        return new AggregateResult(w.GpsInSegmentId, dst, elev, timeS, speedAvg, hrAvg, powerAvg, cadence);
    }

    // Cumulative distance at an ordinal — null channel or NaN value = SQL NULL (double.NaN).
    private static double Cum(DecodedTrack track, int ordinal)
        => track.Distance.IsNull ? double.NaN : track.Distance[ordinal];

    // Optional channel read — whole-NULL channel = whole-column NULL (double.NaN).
    private static double Channel(TrackChannel channel, int ordinal)
        => channel.IsNull ? double.NaN : channel[ordinal];

    // Round5 pin distance at the exact guessed GPS_ID; missing id or NaN coords → NULL (S8.11/12).
    private static double? PinOffsetAtGpsId(DecodedTrack track, int gpsId, double pinLat, double pinLng)
    {
        var (a, b) = track.OrdinalRange(gpsId, gpsId);
        if (a > b) return null;                                                     // GPS_ID absent
        double lat = track.Lat[a], lng = track.Lng[a];
        if (double.IsNaN(lat) || double.IsNaN(lng)) return null;
        return SqliteCompat.Round5(Math.Abs(pinLat - lat) + Math.Abs(pinLng - lng));
    }

    // Winner ordering (S8.9): (dist_score, entry_offset + exit_offset, entry GPS_ID, exit GPS_ID).
    private static bool IsBetter(
        double score, double sum, int entryGps, int exitGps,
        double bestScore, double bestSum, int bestEntryGps, int bestExitGps)
    {
        if (score != bestScore) return score < bestScore;
        if (sum != bestSum)     return sum < bestSum;
        if (entryGps != bestEntryGps) return entryGps < bestEntryGps;
        return exitGps < bestExitGps;
    }
}
