using RouteBaseWeb.Models.Geometry;

namespace RouteBaseWeb.Services.Segments.Rules;

/// <summary>
/// Turns a track's ordered samples into <see cref="TrackEvidence"/> in one geometry pass:
/// zone visit intervals + fly-through segments, and gate crossings. Pure geometry — no EF,
/// no map — so it is unit-testable with hand-built samples.
/// </summary>
public static class TrackEvidenceExtractor
{
    /// <param name="zones">Zone geometries in evaluator order; visits come back in the same order.</param>
    /// <param name="gates">Gate lines in evaluator order; crossings come back in the same order.</param>
    /// <param name="gateDebounceSeconds">Same-gate crossings closer than this are collapsed.</param>
    public static TrackEvidence Extract(
        IReadOnlyList<TrackSample> samples,
        IReadOnlyList<IShape> zones,
        IReadOnlyList<GeoLine>? gates = null,
        double gateDebounceSeconds = 0)
    {
        gates ??= Array.Empty<GeoLine>();

        var visits = new IReadOnlyList<ZoneVisit>[zones.Count];
        var clips = new IReadOnlyList<int>[zones.Count];
        var visitLists = new List<ZoneVisit>?[zones.Count];
        var clipLists = new List<int>?[zones.Count];
        var enterIdx = new int[zones.Count];   // valid while inside[z]
        var inside = new bool[zones.Count];

        var crossings = new IReadOnlyList<GateCrossing>[gates.Count];
        var crossLists = new List<GateCrossing>?[gates.Count];
        var lastKept = new double[gates.Count];
        var hasKept = new bool[gates.Count];

        // A track with no time axis reports 0 for every sample (ShapeSegmentMatcher maps a NaN
        // timestamp to 0), which reads as "every crossing is simultaneous" and would collapse all
        // but the first — a lap gate would yield one crossing and therefore no efforts at all.
        // Debouncing is meaningless without time, so drop it rather than drop the crossings.
        bool hasTime = samples.Count > 1 && samples[^1].TimeSeconds > samples[0].TimeSeconds;
        double debounceSeconds = hasTime ? gateDebounceSeconds : 0;

        // One Contains per (sample, zone): cur[z] holds Contains(samples[i]); the fly-through gate
        // reuses it and computes next[z] once, which becomes cur[z] on the next iteration (was up
        // to 3 Contains per pair — visit toggle + both fly-through endpoints recomputed).
        var cur = new bool[zones.Count];
        var next = new bool[zones.Count];
        if (samples.Count > 0)
            for (int z = 0; z < zones.Count; z++)
                cur[z] = zones[z].Contains(samples[0].Lat, samples[0].Lng);

        for (int i = 0; i < samples.Count; i++)
        {
            var s = samples[i];
            for (int z = 0; z < zones.Count; z++)
            {
                bool now = cur[z];
                if (now && !inside[z])
                {
                    inside[z] = true;          // covers a track that starts inside (enter@0)
                    enterIdx[z] = i;
                }
                else if (!now && inside[z])
                {
                    inside[z] = false;
                    (visitLists[z] ??= new()).Add(new ZoneVisit(enterIdx[z], i - 1));
                }
            }

            if (i + 1 >= samples.Count) continue;
            var n = samples[i + 1];

            // Zone fly-throughs: both endpoints outside but the edge clips the box.
            for (int z = 0; z < zones.Count; z++)
            {
                next[z] = zones[z].Contains(n.Lat, n.Lng);
                if (cur[z] || next[z]) continue;
                if (zones[z].IntersectsSegment(s.Lat, s.Lng, n.Lat, n.Lng))
                    (clipLists[z] ??= new()).Add(i);
            }
            (cur, next) = (next, cur);

            // Gate crossings on segment i, direction-filtered and debounced.
            for (int g = 0; g < gates.Count; g++)
            {
                if (!gates[g].TryCross(s.Lat, s.Lng, n.Lat, n.Lng, out double t, out int dir)) continue;
                if (!gates[g].DirectionAllows(dir)) continue;

                double time = s.TimeSeconds + t * (n.TimeSeconds - s.TimeSeconds);
                if (hasKept[g] && time - lastKept[g] < debounceSeconds) continue;

                (crossLists[g] ??= new()).Add(new GateCrossing(i, time));
                lastKept[g] = time;
                hasKept[g] = true;
            }
        }

        for (int z = 0; z < zones.Count; z++)
        {
            if (inside[z])                     // still inside at the last sample — close the visit
                (visitLists[z] ??= new()).Add(new ZoneVisit(enterIdx[z], samples.Count - 1));
            visits[z] = (IReadOnlyList<ZoneVisit>?)visitLists[z] ?? Array.Empty<ZoneVisit>();
            clips[z] = (IReadOnlyList<int>?)clipLists[z] ?? Array.Empty<int>();
        }
        for (int g = 0; g < gates.Count; g++)
            crossings[g] = (IReadOnlyList<GateCrossing>?)crossLists[g] ?? Array.Empty<GateCrossing>();

        return new TrackEvidence
        {
            Samples = samples,
            ZoneVisits = visits,
            ZoneClipSegments = clips,
            GateCrossings = crossings,
        };
    }
}
