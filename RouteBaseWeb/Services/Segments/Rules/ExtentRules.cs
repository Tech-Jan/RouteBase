using RouteBaseWeb.Models.Geometry;

namespace RouteBaseWeb.Services.Segments.Rules;

/// <summary>
/// Compiled form of an <see cref="ExtentRule"/>: zone numbers resolved to zone indices,
/// ready to run against <see cref="TrackEvidence"/>. Produces the candidate effort spans
/// that membership rules are then evaluated on.
/// </summary>
public interface ICompiledExtentRule
{
    /// <summary>Cheap bbox verdict: false = this track can never yield a span; null = unknown.</summary>
    bool? PreFilter(in GeoBoundingBox trackBbox);

    IReadOnlyList<EffortSpan> BuildSpans(TrackEvidence evidence);
}

/// <summary>The entire track is one effort span.</summary>
public sealed class CompiledWholeExtent : ICompiledExtentRule
{
    public bool? PreFilter(in GeoBoundingBox trackBbox) => null;

    public IReadOnlyList<EffortSpan> BuildSpans(TrackEvidence evidence) =>
        evidence.Samples.Count == 0
            ? Array.Empty<EffortSpan>()
            : new[] { new EffortSpan(0, evidence.Samples.Count - 1) };
}

/// <summary>First-to-last sample inside any of the referenced zones (the old "clip" mode).</summary>
public sealed class CompiledZoneSpanExtent : ICompiledExtentRule
{
    private readonly int[] _zoneIdxs;
    private readonly GeoBoundingBox[] _zoneBboxes;

    public CompiledZoneSpanExtent(int[] zoneIdxs, GeoBoundingBox[] zoneBboxes)
    {
        _zoneIdxs = zoneIdxs;
        _zoneBboxes = zoneBboxes;
    }

    public bool? PreFilter(in GeoBoundingBox trackBbox)
    {
        // No referenced zone can even be reached → no span can exist.
        foreach (var b in _zoneBboxes)
            if (b.Overlaps(trackBbox)) return null;
        return false;
    }

    public IReadOnlyList<EffortSpan> BuildSpans(TrackEvidence evidence)
    {
        int entry = int.MaxValue, exit = int.MinValue;
        foreach (var z in _zoneIdxs)
        {
            foreach (var v in evidence.ZoneVisits[z])
            {
                if (v.EnterIdx < entry) entry = v.EnterIdx;
                if (v.ExitIdx > exit) exit = v.ExitIdx;
            }
        }
        return entry == int.MaxValue
            ? Array.Empty<EffortSpan>()
            : new[] { new EffortSpan(entry, exit) };
    }
}

/// <summary>
/// Efforts between gates: each span runs from a start-gate crossing to the next finish-gate
/// crossing, greedy and non-overlapping. A gate listed as both start and finish is a lap course —
/// crossing it closes the current lap and opens the next. Crossings are already direction-filtered
/// and per-gate debounced in the extractor; <see cref="_minEffortSeconds"/> drops blip spans.
/// </summary>
public sealed class CompiledGatesExtent : ICompiledExtentRule
{
    private readonly int[] _startIdxs;
    private readonly int[] _finishIdxs;
    private readonly GeoBoundingBox[] _startBboxes;
    private readonly GeoBoundingBox[] _finishBboxes;
    private readonly double _minEffortSeconds;

    public CompiledGatesExtent(
        int[] startIdxs, GeoBoundingBox[] startBboxes,
        int[] finishIdxs, GeoBoundingBox[] finishBboxes, double minEffortSeconds)
    {
        _startIdxs = startIdxs;
        _finishIdxs = finishIdxs;
        _startBboxes = startBboxes;
        _finishBboxes = finishBboxes;
        _minEffortSeconds = minEffortSeconds;
    }

    public bool? PreFilter(in GeoBoundingBox trackBbox)
    {
        // A span needs both a start and a finish crossing, so both must at least be reachable.
        if (!AnyOverlap(_startBboxes, trackBbox)) return false;
        if (!AnyOverlap(_finishBboxes, trackBbox)) return false;
        return null;
    }

    public IReadOnlyList<EffortSpan> BuildSpans(TrackEvidence evidence)
    {
        var events = MergeEvents(evidence);
        if (events.Count == 0) return Array.Empty<EffortSpan>();

        List<EffortSpan>? spans = null;
        bool open = false;
        int startSeg = 0;
        double startTime = 0;

        foreach (var e in events)
        {
            if (open && e.IsFinish && e.SegIdx > startSeg && e.Time - startTime >= _minEffortSeconds)
            {
                int entry = startSeg + 1, exit = e.SegIdx;   // between the lines
                if (entry <= exit) (spans ??= new()).Add(new EffortSpan(entry, exit));
                open = false;
            }

            if (!open && e.IsStart)
            {
                open = true;
                startSeg = e.SegIdx;
                startTime = e.Time;
            }
        }

        return (IReadOnlyList<EffortSpan>?)spans ?? Array.Empty<EffortSpan>();
    }

    private readonly record struct GateEvent(int SegIdx, double Time, bool IsStart, bool IsFinish);

    /// <summary>All start/finish crossings merged into one track-ordered event stream. A crossing of
    /// a gate that is both start and finish carries both flags.</summary>
    private List<GateEvent> MergeEvents(TrackEvidence evidence)
    {
        var events = new List<GateEvent>();
        foreach (var idx in _startIdxs)
            foreach (var c in evidence.GateCrossings[idx])
                events.Add(new GateEvent(c.SegIdx, c.TimeSeconds, true, Array.IndexOf(_finishIdxs, idx) >= 0));
        foreach (var idx in _finishIdxs)
        {
            if (Array.IndexOf(_startIdxs, idx) >= 0) continue;   // already added with both flags
            foreach (var c in evidence.GateCrossings[idx])
                events.Add(new GateEvent(c.SegIdx, c.TimeSeconds, false, true));
        }
        events.Sort((a, b) => a.SegIdx != b.SegIdx ? a.SegIdx.CompareTo(b.SegIdx) : a.Time.CompareTo(b.Time));
        return events;
    }

    private static bool AnyOverlap(GeoBoundingBox[] boxes, in GeoBoundingBox track)
    {
        foreach (var b in boxes)
            if (b.Overlaps(track)) return true;
        return false;
    }
}
