using RouteBaseWeb.Models.Geometry;

namespace RouteBaseWeb.Services.Segments.Rules;

/// <summary>
/// Compiled form of a <see cref="MembershipRule"/>. All membership rules are AND'd and
/// evaluated per effort span.
///
/// <see cref="PreFilter"/> is three-valued: <c>false</c> = this track can never satisfy the
/// rule (safe to prune in Stage 1), <c>null</c> = unknown, go to Stage 2. It must never
/// return a wrong <c>false</c> — over-approximation is fine, pruning a real match is not.
/// </summary>
public interface ICompiledMembershipRule
{
    bool? PreFilter(in GeoBoundingBox trackBbox);

    bool Evaluate(TrackEvidence evidence, EffortSpan span);
}

/// <summary>Effort touches at least one of the zones.</summary>
public sealed class CompiledPassesAny : ICompiledMembershipRule
{
    private readonly int[] _zoneIdxs;
    private readonly GeoBoundingBox[] _zoneBboxes;

    public CompiledPassesAny(int[] zoneIdxs, GeoBoundingBox[] zoneBboxes)
    {
        _zoneIdxs = zoneIdxs;
        _zoneBboxes = zoneBboxes;
    }

    public bool? PreFilter(in GeoBoundingBox trackBbox)
    {
        foreach (var b in _zoneBboxes)
            if (b.Overlaps(trackBbox)) return null;
        return false;   // whole track misses every zone's bbox — can't touch any of them
    }

    public bool Evaluate(TrackEvidence evidence, EffortSpan span)
    {
        foreach (var z in _zoneIdxs)
            if (evidence.ZoneTouches(z, span)) return true;
        return false;
    }
}

/// <summary>Effort touches every listed zone.</summary>
public sealed class CompiledPassesAll : ICompiledMembershipRule
{
    private readonly int[] _zoneIdxs;
    private readonly GeoBoundingBox[] _zoneBboxes;

    public CompiledPassesAll(int[] zoneIdxs, GeoBoundingBox[] zoneBboxes)
    {
        _zoneIdxs = zoneIdxs;
        _zoneBboxes = zoneBboxes;
    }

    public bool? PreFilter(in GeoBoundingBox trackBbox)
    {
        foreach (var b in _zoneBboxes)
            if (!b.Overlaps(trackBbox)) return false;   // one required zone is unreachable
        return null;
    }

    public bool Evaluate(TrackEvidence evidence, EffortSpan span)
    {
        foreach (var z in _zoneIdxs)
            if (!evidence.ZoneTouches(z, span)) return false;
        return true;
    }
}

/// <summary>Every sample of the effort lies inside the union of the listed zones ("never leaves").</summary>
public sealed class CompiledStaysWithin : ICompiledMembershipRule
{
    private readonly int[] _zoneIdxs;
    private readonly GeoBoundingBox _unionBbox;
    private readonly bool _wholeExtent;

    public CompiledStaysWithin(int[] zoneIdxs, GeoBoundingBox unionBbox, bool wholeExtent)
    {
        _zoneIdxs = zoneIdxs;
        _unionBbox = unionBbox;
        _wholeExtent = wholeExtent;
    }

    public bool? PreFilter(in GeoBoundingBox trackBbox)
    {
        // Only under "whole" extent is the whole-track bbox the effort's bbox; a clipped
        // span may stay within even though the rest of the track leaves → unknown then.
        if (_wholeExtent && !_unionBbox.Contains(trackBbox)) return false;
        return null;
    }

    public bool Evaluate(TrackEvidence evidence, EffortSpan span) =>
        SpanCoverage.Covers(evidence, _zoneIdxs, span);
}

/// <summary>Effort touches none of the listed zones ("exclusion box").</summary>
public sealed class CompiledAvoids : ICompiledMembershipRule
{
    private readonly int[] _zoneIdxs;

    public CompiledAvoids(int[] zoneIdxs) => _zoneIdxs = zoneIdxs;

    // Bbox overlap is inconclusive (near miss vs hit) and non-overlap means the rule passes —
    // neither outcome justifies pruning the track.
    public bool? PreFilter(in GeoBoundingBox trackBbox) => null;

    public bool Evaluate(TrackEvidence evidence, EffortSpan span)
    {
        foreach (var z in _zoneIdxs)
            if (evidence.ZoneTouches(z, span)) return false;
        return true;
    }
}

/// <summary>Effort opens on a crossing of one of the listed gates (its first segment).</summary>
public sealed class CompiledStartsAtGate : ICompiledMembershipRule
{
    private readonly int[] _gateIdxs;
    private readonly GeoBoundingBox[] _gateBboxes;

    public CompiledStartsAtGate(int[] gateIdxs, GeoBoundingBox[] gateBboxes)
    {
        _gateIdxs = gateIdxs;
        _gateBboxes = gateBboxes;
    }

    public bool? PreFilter(in GeoBoundingBox trackBbox)
    {
        foreach (var b in _gateBboxes)
            if (b.Overlaps(trackBbox)) return null;
        return false;   // no listed gate is even reachable → can't start at one
    }

    // The span's opening segment is EntryIdx-1 (gate-built) or EntryIdx (whole/zoneSpan start).
    public bool Evaluate(TrackEvidence evidence, EffortSpan span)
    {
        foreach (var g in _gateIdxs)
            if (evidence.GateCrossedInSegRange(g, span.EntryIdx - 1, span.EntryIdx)) return true;
        return false;
    }
}

/// <summary>Effort closes on a crossing of one of the listed gates (its last segment).</summary>
public sealed class CompiledEndsAtGate : ICompiledMembershipRule
{
    private readonly int[] _gateIdxs;
    private readonly GeoBoundingBox[] _gateBboxes;

    public CompiledEndsAtGate(int[] gateIdxs, GeoBoundingBox[] gateBboxes)
    {
        _gateIdxs = gateIdxs;
        _gateBboxes = gateBboxes;
    }

    public bool? PreFilter(in GeoBoundingBox trackBbox)
    {
        foreach (var b in _gateBboxes)
            if (b.Overlaps(trackBbox)) return null;
        return false;
    }

    // The span's closing segment is ExitIdx-1..ExitIdx (gate-built exit sits on the finish segment).
    public bool Evaluate(TrackEvidence evidence, EffortSpan span)
    {
        foreach (var g in _gateIdxs)
            if (evidence.GateCrossedInSegRange(g, span.ExitIdx - 1, span.ExitIdx)) return true;
        return false;
    }
}

/// <summary>The effort's first/last sample lies inside one of the zones (OR) — zone analogue of the
/// start/finish gate rules. <see cref="Endpoint.Either"/> passes when start OR finish qualifies.</summary>
public sealed class CompiledEndpointInZone : ICompiledMembershipRule
{
    private readonly int[] _zoneIdxs;
    private readonly GeoBoundingBox[] _zoneBboxes;
    private readonly Endpoint _endpoint;

    public CompiledEndpointInZone(int[] zoneIdxs, GeoBoundingBox[] zoneBboxes, Endpoint endpoint)
    {
        _zoneIdxs = zoneIdxs;
        _zoneBboxes = zoneBboxes;
        _endpoint = endpoint;
    }

    // Endpoint-in-zone implies the track bbox overlaps that zone's bbox → same safe over-approximation
    // as CompiledPassesAny: no overlap with any listed zone means no endpoint can land inside.
    public bool? PreFilter(in GeoBoundingBox trackBbox)
    {
        foreach (var b in _zoneBboxes)
            if (b.Overlaps(trackBbox)) return null;
        return false;
    }

    public bool Evaluate(TrackEvidence evidence, EffortSpan span)
    {
        foreach (var z in _zoneIdxs)
        {
            if (_endpoint != Endpoint.Finish && evidence.ZoneContainsSample(z, span.EntryIdx)) return true;
            if (_endpoint != Endpoint.Start && evidence.ZoneContainsSample(z, span.ExitIdx)) return true;
        }
        return false;
    }
}

/// <summary>Interval sweep shared by staysWithin and the inside-point counting.</summary>
internal static class SpanCoverage
{
    /// <summary>True when [span.EntryIdx, span.ExitIdx] is fully covered by the union of the zones' visits.</summary>
    public static bool Covers(TrackEvidence evidence, int[] zoneIdxs, EffortSpan span)
    {
        var visits = CollectSorted(evidence, zoneIdxs, span);
        int covered = span.EntryIdx - 1;
        foreach (var v in visits)
        {
            if (v.EnterIdx > covered + 1) return false;   // gap before this visit starts
            if (v.ExitIdx > covered) covered = v.ExitIdx;
            if (covered >= span.ExitIdx) return true;
        }
        return covered >= span.ExitIdx;
    }

    /// <summary>Number of samples in the span that lie inside at least one of the zones.</summary>
    public static int CountInside(TrackEvidence evidence, int[] zoneIdxs, EffortSpan span)
    {
        var visits = CollectSorted(evidence, zoneIdxs, span);
        int count = 0, covered = span.EntryIdx - 1;
        foreach (var v in visits)
        {
            int from = Math.Max(v.EnterIdx, covered + 1);
            int to = Math.Min(v.ExitIdx, span.ExitIdx);
            if (to >= from) { count += to - from + 1; covered = to; }
        }
        return count;
    }

    private static List<ZoneVisit> CollectSorted(TrackEvidence evidence, int[] zoneIdxs, EffortSpan span)
    {
        var visits = new List<ZoneVisit>();
        foreach (var z in zoneIdxs)
            foreach (var v in evidence.ZoneVisits[z])
                if (v.EnterIdx <= span.ExitIdx && v.ExitIdx >= span.EntryIdx)
                    visits.Add(v);
        visits.Sort((a, b) => a.EnterIdx.CompareTo(b.EnterIdx));
        return visits;
    }
}
