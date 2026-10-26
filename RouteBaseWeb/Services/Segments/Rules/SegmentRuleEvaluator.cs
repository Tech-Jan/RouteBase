using RouteBaseWeb.Models.Geometry;

namespace RouteBaseWeb.Services.Segments.Rules;

/// <summary>One surviving effort with its sample span and how many of its samples lie inside any zone.</summary>
public readonly record struct EffortResult(EffortSpan Span, int InsidePoints);

/// <summary>
/// A <see cref="RuleDocument"/> compiled once per segment: shape numbers resolved to zone
/// indices, extent + membership rules instantiated. Drives both matcher stages:
///   Stage 1 — <see cref="CouldMatch"/> against Activity_Bbox (never prunes a real match).
///   Stage 2 — <see cref="Evaluate"/> over the extracted <see cref="TrackEvidence"/>.
/// Pure and stateless after compilation.
/// </summary>
public sealed class SegmentRuleEvaluator
{
    private readonly ICompiledExtentRule _extent;
    private readonly ICompiledMembershipRule[] _membership;
    private readonly int[] _allZoneIdxs;

    /// <summary>Zone geometries in compilation order — feed these to <see cref="TrackEvidenceExtractor"/>.</summary>
    public IReadOnlyList<IShape> Zones { get; }

    /// <summary>Gate lines in compilation order — feed these to <see cref="TrackEvidenceExtractor"/>.</summary>
    public IReadOnlyList<GeoLine> Gates { get; }

    /// <summary>Same-gate debounce window (seconds) the extractor should use for this document.</summary>
    public double GateDebounceSeconds { get; }

    /// <summary>Non-spatial track filters, parsed straight from the document. The matcher turns these
    /// into candidate-loading SQL predicates; the evaluator itself stays SQL-free and geometry-only.</summary>
    public IReadOnlyList<TrackFilter> TrackFilters { get; }

    public int ShapeCount { get; }

    private SegmentRuleEvaluator(
        IReadOnlyList<IShape> zones, IReadOnlyList<GeoLine> gates, double gateDebounceSeconds,
        ICompiledExtentRule extent, ICompiledMembershipRule[] membership, int shapeCount,
        IReadOnlyList<TrackFilter> trackFilters)
    {
        Zones = zones;
        Gates = gates;
        GateDebounceSeconds = gateDebounceSeconds;
        _extent = extent;
        _membership = membership;
        _allZoneIdxs = Enumerable.Range(0, zones.Count).ToArray();
        ShapeCount = shapeCount;
        TrackFilters = trackFilters;
    }

    /// <summary>Compile a validated document. Throws on structural errors — call <see cref="RuleDocument.Validate"/> first.</summary>
    public static SegmentRuleEvaluator Compile(RuleDocument doc)
    {
        // Zones and gates live in separate index spaces; a shape number resolves into one or the other.
        var zones = new List<IShape>();
        var zoneBboxes = new List<GeoBoundingBox>();
        var zoneIdxByNo = new Dictionary<int, int>();
        var gates = new List<GeoLine>();
        var gateBboxes = new List<GeoBoundingBox>();
        var gateIdxByNo = new Dictionary<int, int>();

        foreach (var s in doc.Shapes)
        {
            switch (s.Geom)
            {
                case RectGeom rect:
                    var shape = rect.ToShape();
                    zoneIdxByNo[s.No] = zones.Count;
                    zones.Add(shape);
                    zoneBboxes.Add(shape.Bbox);
                    break;
                case LineGeom line:
                    var gate = line.ToLine();
                    gateIdxByNo[s.No] = gates.Count;
                    gates.Add(gate);
                    gateBboxes.Add(gate.Bbox);
                    break;
                default:
                    throw new InvalidOperationException($"Shape {s.No} has no usable geometry");
            }
        }

        (int[] idxs, GeoBoundingBox[] boxes) ResolveZones(List<int> nos)
        {
            var idxs = nos.Select(n => zoneIdxByNo[n]).ToArray();
            return (idxs, idxs.Select(i => zoneBboxes[i]).ToArray());
        }
        (int[] idxs, GeoBoundingBox[] boxes) ResolveGates(List<int> nos)
        {
            var idxs = nos.Select(n => gateIdxByNo[n]).ToArray();
            return (idxs, idxs.Select(i => gateBboxes[i]).ToArray());
        }

        bool wholeExtent = doc.Extent is WholeExtent;
        ICompiledExtentRule extent = doc.Extent switch
        {
            WholeExtent => new CompiledWholeExtent(),
            ZoneSpanExtent zs => new CompiledZoneSpanExtent(ResolveZones(zs.Zones).idxs, ResolveZones(zs.Zones).boxes),
            GatesExtent g => BuildGatesExtent(g),
            _ => throw new InvalidOperationException($"Unknown extent rule {doc.Extent.GetType().Name}"),
        };

        var membership = doc.Membership.Select<MembershipRule, ICompiledMembershipRule>(m => m switch
        {
            PassesAnyRule r => new CompiledPassesAny(ResolveZones(r.Zones).idxs, ResolveZones(r.Zones).boxes),
            PassesAllRule r => new CompiledPassesAll(ResolveZones(r.Zones).idxs, ResolveZones(r.Zones).boxes),
            StaysWithinRule r => Within(r),
            AvoidsRule r => new CompiledAvoids(ResolveZones(r.Zones).idxs),
            StartsAtGateRule r => new CompiledStartsAtGate(ResolveGates(r.Zones).idxs, ResolveGates(r.Zones).boxes),
            EndsAtGateRule r => new CompiledEndsAtGate(ResolveGates(r.Zones).idxs, ResolveGates(r.Zones).boxes),
            EndpointInZoneRule r => new CompiledEndpointInZone(ResolveZones(r.Zones).idxs, ResolveZones(r.Zones).boxes, r.Endpoint),
            _ => throw new InvalidOperationException($"Unknown membership rule {m.GetType().Name}"),
        }).ToArray();

        return new SegmentRuleEvaluator(
            zones, gates, doc.GateDebounceSeconds, extent, membership, doc.Shapes.Count, doc.TrackFilters);

        CompiledStaysWithin Within(StaysWithinRule r)
        {
            var (idxs, boxes) = ResolveZones(r.Zones);
            return new CompiledStaysWithin(idxs, Union(boxes), wholeExtent);
        }

        CompiledGatesExtent BuildGatesExtent(GatesExtent g)
        {
            var (startIdxs, startBoxes) = ResolveGates(g.StartGates);
            var (finishIdxs, finishBoxes) = ResolveGates(g.FinishGates);
            return new CompiledGatesExtent(startIdxs, startBoxes, finishIdxs, finishBoxes, doc.MinEffortSeconds);
        }
    }

    /// <summary>Parse + validate + compile in one step; null when json is missing or invalid.</summary>
    public static SegmentRuleEvaluator? TryCompile(string? json, out List<string> errors)
    {
        errors = new List<string>();
        var doc = RuleDocument.TryParse(json);
        if (doc == null) { errors.Add("Rules JSON missing or malformed"); return null; }
        errors = doc.Validate();
        return errors.Count == 0 ? Compile(doc) : null;
    }

    /// <summary>Stage-1 verdict: false only when no effort of this track could ever survive.</summary>
    public bool CouldMatch(in GeoBoundingBox trackBbox)
    {
        if (_extent.PreFilter(trackBbox) == false) return false;
        foreach (var rule in _membership)
            if (rule.PreFilter(trackBbox) == false) return false;
        return true;
    }

    /// <summary>Stage-2 verdict: the effort spans that survive every membership rule.</summary>
    public IReadOnlyList<EffortResult> Evaluate(TrackEvidence evidence)
    {
        List<EffortResult>? results = null;
        foreach (var span in _extent.BuildSpans(evidence))
        {
            bool ok = true;
            foreach (var rule in _membership)
                if (!rule.Evaluate(evidence, span)) { ok = false; break; }
            if (!ok) continue;

            (results ??= new()).Add(new EffortResult(
                span, SpanCoverage.CountInside(evidence, _allZoneIdxs, span)));
        }
        return (IReadOnlyList<EffortResult>?)results ?? Array.Empty<EffortResult>();
    }

    private static GeoBoundingBox Union(GeoBoundingBox[] boxes)
    {
        double minLat = double.MaxValue, maxLat = double.MinValue, minLng = double.MaxValue, maxLng = double.MinValue;
        foreach (var b in boxes)
        {
            minLat = Math.Min(minLat, b.MinLat); maxLat = Math.Max(maxLat, b.MaxLat);
            minLng = Math.Min(minLng, b.MinLong); maxLng = Math.Max(maxLng, b.MaxLong);
        }
        return new GeoBoundingBox(minLat, maxLat, minLng, maxLng);
    }
}
