using RouteBaseWeb.Models;
using System.Numerics;
using RouteBaseWeb.Services.Tracks;

namespace RouteBaseWeb.Services.Matching;

/// <summary>
/// Pure, I/O-free port of SQL pipeline Stages 3–6 + the INSERT gate
/// (SegmentService.Pipeline.cs). One decoded track is matched against one segment in a
/// single streaming pass; no DbContext, no temp tables.
///
/// PARITY (invariant 2, bug-for-bug): every branch, tolerance and truncation reproduces the
/// SQL quirks catalogued in analysis.md §TE-07 exactly — including the "wrong-looking" ones
/// (>250 vs ≥250 zone/ultra split, strict `>` gap grouping in GPS_ID units, MAX−MIN effort
/// distance, −1 sentinel passing the distance gate, span-not-count).
/// </summary>
public static class MatchEngineCore
{
    // S4.5: exact tolerance literals.
    private const double TightTol  = 0.00015;   // start_end_tight
    private const double ShortTol  = 0.0002;    // short_standard
    private const double MediumTol = 0.0003;    // middle_medium
    private const double UltraTol  = 0.002;     // middle_ultra_wide

    /// <summary>
    /// Returns one <see cref="QualifiedCrossing"/> per crossing window (pre-gate). Apply
    /// <see cref="PassesGate"/> to decide which are inserted.
    /// <paramref name="trackId"/> is the activity these points belong to — supplied by the caller because
    /// the decode is shared content and carries no id. It ends up on the effort rows, so it must be the
    /// activity actually being matched, not whichever one first decoded these bytes.
    /// </summary>
    public static List<QualifiedCrossing> Match(SegmentGeometry seg, DecodedTrack track, TrackId trackId)
    {
        var result = new List<QualifiedCrossing>();
        int n = seg.TotalPoints;
        int words = (n + 63) >> 6;

        // Reusable per-window state (S6.3 distinct-seg-point counts via bitmaps over ordinals).
        var matched   = new ulong[words];
        var startTight = new ulong[words];
        var endTight   = new ulong[words];
        bool windowOpen = false;
        int entryGpsId = 0, exitGpsId = 0, prevGpsId = 0;

        void Flush()
        {
            int matchedCount = PopCount(matched);
            int matchPerc   = (int)(matchedCount * 100.0 / n);            // S6.2 truncates
            double effort   = EffortDistance(track, entryGpsId, exitGpsId); // S6.1
            result.Add(new QualifiedCrossing(
                trackId.Value, seg.SegmentId.Value,
                entryGpsId, exitGpsId,
                matchedCount, n, matchPerc,
                effort,
                PopCount(startTight), PopCount(endTight)));
            Array.Clear(matched, 0, words);
            Array.Clear(startTight, 0, words);
            Array.Clear(endTight, 0, words);
        }

        for (int i = 0; i < track.Count; i++)
        {
            double gLat = track.Lat[i], gLng = track.Lng[i];
            if (double.IsNaN(gLat) || double.IsNaN(gLng)) continue;      // S4.3

            if (!seg.CellIndex.TryGetValue(GeoCellHash.Pack(gLat, gLng), out var candidates))
                continue;

            int gpsId = i;
            bool producedEvent = false;

            foreach (int j in candidates)
            {
                if (!Classify(seg, n, gLat, gLng, j, out bool isTight)) continue;

                // First matched event of this GPS_ID may open/close a window (S5.1/S5.2).
                if (!producedEvent)
                {
                    if (!windowOpen)
                    {
                        windowOpen = true;
                        entryGpsId = gpsId;
                    }
                    else if (gpsId - prevGpsId > seg.GapThreshold)   // strict > , GPS_ID units
                    {
                        Flush();
                        entryGpsId = gpsId;
                    }
                    exitGpsId = gpsId;
                    prevGpsId = gpsId;
                    producedEvent = true;
                }

                SetBit(matched, j);
                if (isTight)
                {
                    long sid = seg.SegGpsIds[j];
                    if (sid >= seg.SegStartId && sid <= seg.StartZoneEnd) SetBit(startTight, j);
                    if (sid >= seg.EndZoneStart && sid <= seg.SegEndId)   SetBit(endTight, j);
                }
            }
        }

        if (windowOpen) Flush();
        return result;
    }

    /// <summary>
    /// Classifies a (GPS point, segment point) pair by S4.1's CASE-order branches. Returns
    /// false when no branch matches (row dropped). <paramref name="isTight"/> is set only for
    /// the first branch (start_end_tight) — the one that feeds zone-hit counts.
    /// </summary>
    private static bool Classify(SegmentGeometry seg, int n, double gLat, double gLng, int j, out bool isTight)
    {
        isTight = false;
        double dLat = Math.Abs(gLat - seg.Lat[j]);   // NaN seg coord → NaN → all `<` false → drop
        double dLng = Math.Abs(gLng - seg.Lng[j]);
        long sid = seg.SegGpsIds[j];
        bool inZone   = sid <= seg.StartZoneEnd || sid >= seg.EndZoneStart;
        bool inMiddle = sid > seg.StartZoneEnd && sid < seg.EndZoneStart;

        // Branch order == match_type priority (first matching branch wins).
        if (inZone && dLat < TightTol && dLng < TightTol)
        {
            isTight = true;
            return true;
        }
        if (n < 100 && dLat < ShortTol && dLng < ShortTol)
            return true;
        if (n >= 100 && n < 250 && inMiddle && dLat < MediumTol && dLng < MediumTol)
            return true;
        if (n >= 250 && inMiddle && dLat < UltraTol && dLng < UltraTol)
            return true;
        return false;
    }

    /// <summary>
    /// S7.1 INSERT gate, verbatim. <paramref name="segmentDistance"/> null (missing Segment /
    /// NULL distance) passes the distance gate; a −1 effort distance (no distance data) also
    /// passes it.
    /// </summary>
    public static bool PassesGate(QualifiedCrossing qc, double? segmentDistance, int threshold)
    {
        return qc.StartZoneHits > 0
            && qc.EndZoneHits > 0
            && (qc.GpsIdExit - qc.GpsIdEntry) >= Math.Max(5, (int)(qc.TotalPoints * 0.1))
            && qc.MatchPerc >= threshold
            && (qc.TotalPoints < 100 || (qc.StartZoneHits >= 2 && qc.EndZoneHits >= 2))
            && (qc.EffortDistM < 0 || segmentDistance is null || qc.EffortDistM >= segmentDistance * 0.60);
    }

    // S6.1: MAX(distance) − MIN(distance) over all track points in [entry..exit] with a
    // non-NULL distance; none → −1. NaN = NULL (skipped). MAX−MIN, not last−first.
    private static double EffortDistance(DecodedTrack track, int entryGpsId, int exitGpsId)
    {
        if (track.Distance.IsNull) return -1;
        var (lo, hi) = track.OrdinalRange(entryGpsId, exitGpsId);
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        bool any = false;
        for (int k = lo; k <= hi; k++)
        {
            double d = track.Distance[k];
            if (double.IsNaN(d)) continue;
            if (d < min) min = d;
            if (d > max) max = d;
            any = true;
        }
        return any ? max - min : -1;
    }

    private static void SetBit(ulong[] bits, int j) => bits[j >> 6] |= 1UL << (j & 63);

    private static int PopCount(ulong[] bits)
    {
        int c = 0;
        for (int w = 0; w < bits.Length; w++) c += BitOperations.PopCount(bits[w]);
        return c;
    }
}
