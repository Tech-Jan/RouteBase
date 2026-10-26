namespace RouteBaseWeb.Services.Segments.Rules;

/// <summary>One GPS sample of a track, in recorded order. Index in the sample array is the
/// coordinate every other evidence type speaks in; <see cref="GpsId"/> maps back to the DB.
/// <see cref="TimeSeconds"/> is an epoch-relative timestamp used for gate debounce / min-effort
/// (0 when a track has no timestamps — harmless for zone-only rules).</summary>
public readonly record struct TrackSample(int GpsId, double Lat, double Lng, double TimeSeconds = 0);

/// <summary>A contiguous run of samples inside one zone: [EnterIdx, ExitIdx], inclusive.</summary>
public readonly record struct ZoneVisit(int EnterIdx, int ExitIdx);

/// <summary>A gate crossing on track segment <see cref="SegIdx"/> (between samples SegIdx and SegIdx+1),
/// already filtered to the gate's allowed direction. <see cref="TimeSeconds"/> is interpolated at the
/// crossing point and drives debounce / min-effort.</summary>
public readonly record struct GateCrossing(int SegIdx, double TimeSeconds);

/// <summary>A candidate effort produced by the extent rule: [EntryIdx, ExitIdx], inclusive sample indices.</summary>
public readonly record struct EffortSpan(int EntryIdx, int ExitIdx);

/// <summary>
/// Everything the rules need to know about one track, extracted in a single GPS pass by
/// <see cref="TrackEvidenceExtractor"/>. Rules are pure combinators over this — they never
/// re-scan GPS points. <see cref="ZoneVisits"/> / <see cref="ZoneClipSegments"/> are indexed by
/// zone position and <see cref="GateCrossings"/> by gate position (the order the evaluator
/// compiled each in), not by shape number.
/// </summary>
public sealed class TrackEvidence
{
    public required IReadOnlyList<TrackSample> Samples { get; init; }

    /// <summary>Per zone: point-containment visit intervals (staysWithin / inside-point counting).</summary>
    public required IReadOnlyList<ZoneVisit>[] ZoneVisits { get; init; }

    /// <summary>Per zone: segment indices where the track flies through the zone between two
    /// outside samples (both endpoints out, edge clips the box). Augments <see cref="ZoneTouches"/>
    /// so sparse sampling can't skip a small box.</summary>
    public required IReadOnlyList<int>[] ZoneClipSegments { get; init; }

    /// <summary>Per gate: crossings in track order.</summary>
    public required IReadOnlyList<GateCrossing>[] GateCrossings { get; init; }

    /// <summary>True when zone <paramref name="zoneIdx"/> is touched anywhere inside the span —
    /// by a point-containment visit or by a fly-through segment fully inside the span.</summary>
    public bool ZoneTouches(int zoneIdx, EffortSpan span)
    {
        foreach (var v in ZoneVisits[zoneIdx])
            if (v.EnterIdx <= span.ExitIdx && v.ExitIdx >= span.EntryIdx) return true;
        foreach (var seg in ZoneClipSegments[zoneIdx])
            if (seg >= span.EntryIdx && seg < span.ExitIdx) return true;   // both endpoints in span
        return false;
    }

    /// <summary>True when sample <paramref name="sampleIdx"/> lies inside zone <paramref name="zoneIdx"/>
    /// (point containment). Fly-through clip segments are intentionally ignored — an effort endpoint
    /// is a real sample, not a pass-through between two outside samples.</summary>
    public bool ZoneContainsSample(int zoneIdx, int sampleIdx)
    {
        foreach (var v in ZoneVisits[zoneIdx])
            if (v.EnterIdx <= sampleIdx && sampleIdx <= v.ExitIdx) return true;
        return false;
    }

    /// <summary>True when gate <paramref name="gateIdx"/> is crossed on a segment in
    /// [<paramref name="loSeg"/>, <paramref name="hiSeg"/>] (inclusive segment indices).</summary>
    public bool GateCrossedInSegRange(int gateIdx, int loSeg, int hiSeg)
    {
        foreach (var c in GateCrossings[gateIdx])
            if (c.SegIdx >= loSeg && c.SegIdx <= hiSeg) return true;
        return false;
    }
}
