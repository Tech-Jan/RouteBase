using RouteBaseWeb.Services.Segments.Rules;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Rules;

/// <summary>
/// Boundary + range coverage for the RuleDocument sub-components' <c>Validate</c> methods
/// (rect/line geometry ranges, membership-rule zone/gate references and caps, extent-rule
/// references). Mutation testing (2026-08-02) found these lines executed by the
/// document-level suite but with no assertion pinning the actual boundary — every case here
/// isolates one coordinate/limit so an off-by-one or flipped comparison is caught.
/// </summary>
public class RuleStructuralValidationTests
{
    // ------------------------------------------------------------------ RectGeom ranges

    // Each row isolates ONE coordinate at (or just past) its limit while the others sit safely
    // inside range, so a single flipped comparison (<-90 → <=-90, >90 → >=90, || → &&) flips
    // exactly this row's expected outcome. Lat limits are inclusive [-90,90]; lng [-180,180].
    [Theory]
    // all in range → valid
    [InlineData(0.0, 0.0, 1.0, 1.0, false)]
    // South at each inclusive limit (others in range) → valid
    [InlineData(-90.0, 0.0, 1.0, 1.0, false)]
    [InlineData(90.0, 0.0, 89.0, 1.0, false)]
    // North at each inclusive limit → valid
    [InlineData(0.0, 0.0, -90.0, 1.0, false)]
    [InlineData(0.0, 0.0, 90.0, 1.0, false)]
    // West at each inclusive limit → valid
    [InlineData(0.0, -180.0, 1.0, 1.0, false)]
    [InlineData(0.0, 180.0, 1.0, 1.0, false)]
    // East at each inclusive limit → valid
    [InlineData(0.0, 0.0, 1.0, -180.0, false)]
    [InlineData(0.0, 0.0, 1.0, 180.0, false)]
    // exactly one coordinate just past its limit → error (isolates every || term and every > / <)
    [InlineData(-90.0001, 0.0, 1.0, 1.0, true)]   // South below min
    [InlineData(90.0001, 0.0, 1.0, 1.0, true)]    // South above max
    [InlineData(0.0, 0.0, -90.0001, 1.0, true)]   // North below min
    [InlineData(0.0, 0.0, 90.0001, 1.0, true)]    // North above max
    [InlineData(0.0, -180.0001, 1.0, 1.0, true)]  // West below min
    [InlineData(0.0, 180.0001, 1.0, 1.0, true)]   // West above max
    [InlineData(0.0, 0.0, 1.0, -180.0001, true)]  // East below min
    [InlineData(0.0, 0.0, 1.0, 180.0001, true)]   // East above max
    public void RectGeom_Validate_RangeBoundaries(double south, double west, double north, double east, bool expectError)
    {
        var errors = new RectGeom { South = south, West = west, North = north, East = east }.Validate(7).ToList();

        if (expectError)
        {
            var msg = Assert.Single(errors);
            Assert.Contains("out of range", msg, StringComparison.Ordinal);
        }
        else
        {
            Assert.Empty(errors);
        }
    }

    // ------------------------------------------------------------------ LineGeom ranges + single point

    [Theory]
    // distinct endpoints, all in range → valid
    [InlineData(0.0, 0.0, 1.0, 1.0, false)]
    // each latitude at an inclusive limit (endpoints kept distinct) → valid
    [InlineData(-90.0, 0.0, 1.0, 1.0, false)]   // Lat1 min
    [InlineData(90.0, 0.0, 1.0, 1.0, false)]    // Lat1 max
    [InlineData(0.0, 0.0, -90.0, 1.0, false)]   // Lat2 min
    [InlineData(0.0, 0.0, 90.0, 1.0, false)]    // Lat2 max
    // each longitude at an inclusive limit → valid
    [InlineData(0.0, -180.0, 1.0, 0.0, false)]  // Lng1 min
    [InlineData(0.0, 180.0, 1.0, 0.0, false)]   // Lng1 max
    [InlineData(0.0, 0.0, 1.0, -180.0, false)]  // Lng2 min
    [InlineData(0.0, 0.0, 1.0, 180.0, false)]   // Lng2 max
    // exactly one endpoint coordinate just past its limit → error (isolates every || term)
    [InlineData(90.0001, 0.0, 1.0, 1.0, true)]   // Lat1 above max
    [InlineData(-90.0001, 0.0, 1.0, 1.0, true)]  // Lat1 below min
    [InlineData(0.0, 0.0, 90.0001, 1.0, true)]   // Lat2 above max
    [InlineData(0.0, 0.0, -90.0001, 1.0, true)]  // Lat2 below min
    [InlineData(0.0, 180.0001, 1.0, 0.0, true)]  // Lng1 above max
    [InlineData(0.0, -180.0001, 1.0, 0.0, true)] // Lng1 below min
    [InlineData(0.0, 0.0, 1.0, 180.0001, true)]  // Lng2 above max
    [InlineData(0.0, 0.0, 1.0, -180.0001, true)] // Lng2 below min
    public void LineGeom_Validate_RangeBoundaries(double lat1, double lng1, double lat2, double lng2, bool expectError)
    {
        var errors = new LineGeom { Lat1 = lat1, Lng1 = lng1, Lat2 = lat2, Lng2 = lng2 }.Validate(9).ToList();

        if (expectError)
        {
            Assert.Contains(errors, m => m.Contains("gate endpoints out of range", StringComparison.Ordinal));
        }
        else
        {
            Assert.Empty(errors);
        }
    }

    [Fact]
    public void LineGeom_Validate_DegenerateSinglePoint_Rejected()
    {
        // Both endpoints identical AND in range → the range check passes but the single-point check fires.
        var errors = new LineGeom { Lat1 = 5, Lng1 = 5, Lat2 = 5, Lng2 = 5 }.Validate(3).ToList();

        var msg = Assert.Single(errors);
        Assert.Contains("gate is a single point", msg, StringComparison.Ordinal);
    }

    [Theory]
    // shares one coordinate but not both → a real (degenerate-looking but legal) axis-aligned gate, NOT a point
    [InlineData(5.0, 5.0, 5.0, 6.0)]   // same lat, different lng (horizontal gate)
    [InlineData(5.0, 5.0, 6.0, 5.0)]   // same lng, different lat (vertical gate)
    public void LineGeom_Validate_AxisAlignedGate_IsNotASinglePoint(double lat1, double lng1, double lat2, double lng2)
    {
        var errors = new LineGeom { Lat1 = lat1, Lng1 = lng1, Lat2 = lat2, Lng2 = lng2 }.Validate(3).ToList();

        Assert.DoesNotContain(errors, m => m.Contains("single point", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ MembershipRule refs / caps

    private static ShapeCatalog Catalog(params (int no, string kind)[] shapes) =>
        new(shapes.Select(s => s.no).ToHashSet(),
            shapes.ToDictionary(s => s.no, s => s.kind));

    [Fact]
    public void Membership_ZoneRule_EmptyZones_UsesZoneNoun()
    {
        var errors = new PassesAnyRule().Validate(Catalog()).ToList();
        Assert.Contains(errors, e => e.Contains("PassesAnyRule: needs at least one zone", StringComparison.Ordinal));
    }

    [Fact]
    public void Membership_GateRule_EmptyZones_UsesGateNoun()
    {
        // Pins the WantsGate ? Gate : Zone noun selection: a gate rule must say "gate", not "zone".
        var errors = new StartsAtGateRule().Validate(Catalog()).ToList();
        Assert.Contains(errors, e => e.Contains("StartsAtGateRule: needs at least one gate", StringComparison.Ordinal));
    }

    [Fact]
    public void Membership_ExactlyMaxZones_NotRejected()
    {
        // Boundary: exactly MaxZonesPerRule references is allowed; MaxZonesPerRule + 1 is not.
        var zones = Enumerable.Range(1, RuleDocument.MaxZonesPerRule).ToList();
        var catalog = Catalog(zones.Select(z => (z, ShapeKinds.Zone)).ToArray());

        var errors = new PassesAnyRule { Zones = zones }.Validate(catalog).ToList();

        Assert.DoesNotContain(errors, e => e.Contains("too many", StringComparison.Ordinal));
        Assert.Empty(errors);
    }

    [Fact]
    public void Membership_OverMaxZones_Rejected()
    {
        var zones = Enumerable.Range(1, RuleDocument.MaxZonesPerRule + 1).ToList();
        var catalog = Catalog(zones.Select(z => (z, ShapeKinds.Zone)).ToArray());

        var errors = new PassesAnyRule { Zones = zones }.Validate(catalog).ToList();

        Assert.Contains(errors, e => e.Contains($"too many zones (max {RuleDocument.MaxZonesPerRule})", StringComparison.Ordinal));
    }

    [Fact]
    public void Membership_EndsAtGate_ReferencingGate_IsValid()
    {
        // Pins EndsAtGateRule.WantsGate == true: a gate reference must satisfy it. If WantsGate flipped to
        // false the rule would demand a zone and reject this gate.
        var catalog = Catalog((1, ShapeKinds.Gate));
        var errors = new EndsAtGateRule { Zones = { 1 } }.Validate(catalog).ToList();
        Assert.Empty(errors);
    }

    [Fact]
    public void Membership_EndsAtGate_ReferencingZone_Rejected()
    {
        var catalog = Catalog((1, ShapeKinds.Zone));
        var errors = new EndsAtGateRule { Zones = { 1 } }.Validate(catalog).ToList();
        Assert.Contains(errors, e => e.Contains("shape 1 is not a gate", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------ ZoneSpanExtent refs

    [Fact]
    public void ZoneSpanExtent_UnknownShape_Rejected()
    {
        var catalog = Catalog((1, ShapeKinds.Zone));
        var errors = new ZoneSpanExtent { Zones = { 99 } }.Validate(catalog).ToList();
        Assert.Contains(errors, e => e.Contains("zoneSpan extent references unknown shape 99", StringComparison.Ordinal));
    }

    [Fact]
    public void ZoneSpanExtent_ReferencingGate_Rejected()
    {
        var catalog = Catalog((1, ShapeKinds.Zone), (5, ShapeKinds.Gate));
        var errors = new ZoneSpanExtent { Zones = { 5 } }.Validate(catalog).ToList();
        Assert.Contains(errors, e => e.Contains("references shape 5 which is not a zone", StringComparison.Ordinal));
    }

    [Fact]
    public void ZoneSpanExtent_ValidZoneReference_NoRefErrors()
    {
        var catalog = Catalog((1, ShapeKinds.Zone));
        var errors = new ZoneSpanExtent { Zones = { 1 } }.Validate(catalog).ToList();
        Assert.Empty(errors);
    }

    // ------------------------------------------------------------------ GatesExtent refs

    [Fact]
    public void GatesExtent_NoStartGate_Rejected()
    {
        var catalog = Catalog((1, ShapeKinds.Gate));
        var errors = new GatesExtent { FinishGates = { 1 } }.Validate(catalog).ToList();
        Assert.Contains(errors, e => e.Contains("needs at least one start gate", StringComparison.Ordinal));
    }

    [Fact]
    public void GatesExtent_ChecksFinishGates_NotOnlyStartGates()
    {
        // Pins the StartGates.Concat(FinishGates) walk: a bad FINISH gate must be flagged. If Concat were
        // replaced by Except, the finish gate would be dropped from the reference walk and this survives.
        var catalog = Catalog((1, ShapeKinds.Gate));
        var errors = new GatesExtent { StartGates = { 1 }, FinishGates = { 99 } }.Validate(catalog).ToList();
        Assert.Contains(errors, e => e.Contains("gates extent references unknown shape 99", StringComparison.Ordinal));
    }

    [Fact]
    public void GatesExtent_ReferencingZoneAsGate_Rejected()
    {
        var catalog = Catalog((1, ShapeKinds.Gate), (5, ShapeKinds.Zone));
        var errors = new GatesExtent { StartGates = { 5 }, FinishGates = { 1 } }.Validate(catalog).ToList();
        Assert.Contains(errors, e => e.Contains("references shape 5 which is not a gate", StringComparison.Ordinal));
    }
}
