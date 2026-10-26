using RouteBaseWeb.Models.Geometry;
using RouteBaseWeb.Models.GridFilter;
using RouteBaseWeb.Services.Segments.Rules;
using Xunit;

namespace RouteBaseWeb.Tests.Services.Rules;

/// <summary>
/// Wire-format contract of the rule document: parse/serialize round-trip, rejection of
/// malformed/unknown-type JSON, and the structural validation every endpoint relies on.
/// </summary>
public class RuleDocumentTests
{
    private static RuleDocument Doc() => new()
    {
        Shapes = new()
        {
            new ShapeDef { No = 1, Geom = new RectGeom { South = 47.0, West = 8.0, North = 47.1, East = 8.1 } },
            new ShapeDef { No = 2, Geom = new RectGeom { South = 47.2, West = 8.2, North = 47.3, East = 8.3 } },
        },
        Extent = new ZoneSpanExtent { Zones = { 1, 2 } },
        Membership = new()
        {
            new PassesAnyRule { Zones = { 1, 2 }, Group = 1 },
            new AvoidsRule { Zones = { 2 } },
        },
    };

    [Fact]
    public void Serialize_Parse_RoundTrips()
    {
        var doc = Doc();
        var parsed = RuleDocument.TryParse(doc.Serialize());

        Assert.NotNull(parsed);
        // Record equality is reference-based for the List members — compare canonical JSON instead.
        Assert.Equal(doc.Serialize(), parsed!.Serialize());
        Assert.Empty(parsed.Validate());
    }

    [Fact]
    public void Parse_ReadsCamelCaseWithDiscriminators()
    {
        const string json = """
            {
              "version": 2,
              "shapes": [ { "no": 1, "kind": "zone", "geom": { "type": "rect", "south": 1, "west": 2, "north": 3, "east": 4 } } ],
              "extent": { "type": "whole" },
              "membership": [ { "zones": [1], "type": "staysWithin" } ]
            }
            """;

        var doc = RuleDocument.TryParse(json);

        Assert.NotNull(doc);
        Assert.Empty(doc!.Validate());
        Assert.IsType<WholeExtent>(doc.Extent);
        var within = Assert.IsType<StaysWithinRule>(Assert.Single(doc.Membership));
        Assert.Equal(new List<int> { 1 }, within.Zones);
    }

    /// <summary>
    /// TS↔C# contract: this JSON mirrors exactly what the frontend's buildRuleDocument
    /// (src/js/overview/shape-filter/rule-document.ts) emits — every field name, discriminator and
    /// casing. If this test breaks, the two hand-mirrored schemas have drifted: fix BOTH sides.
    /// </summary>
    [Fact]
    public void Parse_TsAuthoredDocument_AllFieldsSurvive()
    {
        const string json = """
            {
              "version": 2,
              "shapes": [
                { "no": 1, "kind": "zone", "geom": { "type": "rect", "south": 47.0, "west": 8.0, "north": 47.1, "east": 8.1 } },
                { "no": 2, "kind": "gate", "geom": { "type": "line", "lat1": 47.0, "lng1": 8.0, "lat2": 47.1, "lng2": 8.1, "direction": "forward" } },
                { "no": 3, "kind": "gate", "geom": { "type": "line", "lat1": 47.2, "lng1": 8.2, "lat2": 47.3, "lng2": 8.3, "direction": "any" } }
              ],
              "extent": { "type": "gates", "startGates": [2], "finishGates": [3] },
              "membership": [ { "type": "passesAll", "zones": [1] } ],
              "trackFilters": [
                { "type": "dateRange", "from": "2025-06-01", "to": "2025-06-30" },
                { "type": "activityType", "types": ["Run", "TrailRun"] }
              ]
            }
            """;

        var doc = RuleDocument.TryParse(json);

        Assert.NotNull(doc);
        Assert.Empty(doc!.Validate());
        var gates = Assert.IsType<GatesExtent>(doc.Extent);
        Assert.Equal(new List<int> { 2 }, gates.StartGates);
        Assert.Equal(new List<int> { 3 }, gates.FinishGates);
        var line = Assert.IsType<LineGeom>(doc.Shapes[1].Geom);
        Assert.Equal(Models.Geometry.CrossDirection.Forward, line.Direction);
        var dateFilter = Assert.IsType<DateRangeFilter>(doc.TrackFilters[0]);
        Assert.Equal("2025-06-01", dateFilter.From);
        Assert.Equal("2025-06-30", dateFilter.To);
        var typeFilter = Assert.IsType<ActivityTypeFilter>(doc.TrackFilters[1]);
        Assert.Equal(new List<string> { "Run", "TrailRun" }, typeFilter.Types);

        // Re-serialize and re-parse: the wire names must survive a C#-authored round-trip too.
        var reparsed = RuleDocument.TryParse(doc.Serialize());
        Assert.NotNull(reparsed);
        Assert.Equal(doc.Serialize(), reparsed!.Serialize());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{ "version": 2, "extent": { "type": "teleport" }, "shapes": [] }""")]        // unknown extent
    [InlineData("""{ "version": 2, "membership": [ { "type": "flies" } ], "shapes": [] }""")]   // unknown rule
    public void Parse_RejectsMalformedOrUnknownTypes(string? json)
    {
        Assert.Null(RuleDocument.TryParse(json));
    }

    [Fact]
    public void Validate_FlagsStructuralErrors()
    {
        var doc = new RuleDocument
        {
            Version = 1,                                                       // wrong version
            Shapes = new()
            {
                new ShapeDef { No = 1, Geom = new RectGeom { South = 95, West = 0, North = 96, East = 1 } }, // lat range
                new ShapeDef { No = 1, Geom = null },                          // duplicate no + missing geom
                new ShapeDef { No = 3, Kind = "blob", Geom = new RectGeom() }, // unsupported kind
            },
            Extent = new ZoneSpanExtent(),                                     // no zones
            Membership = { new PassesAllRule { Zones = { 9 } } },              // dangling ref
        };

        var errors = doc.Validate();

        Assert.Contains(errors, e => e.Contains("version 1", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("out of range", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("Duplicate shape number 1", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("no geometry", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("unsupported kind", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("at least one zone", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("unknown shape 9", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RequiresAtLeastOneShape()
    {
        var errors = new RuleDocument().Validate();
        Assert.Contains(errors, e => e.Contains("At least one shape", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- mutation-hardening: parse + doc caps + null elements

    [Fact]
    public void Parse_IsCaseInsensitive_ForPropertyNames()
    {
        // PropertyNameCaseInsensitive = true: PascalCase property names must still bind (discriminators
        // stay lowercase). With it off, "Version"/"Shapes" miss and the doc silently falls back to defaults.
        const string json = """
            {
              "Version": 2,
              "Shapes": [ { "No": 1, "Kind": "zone", "Geom": { "type": "rect", "South": 1, "West": 2, "North": 3, "East": 4 } } ],
              "Extent": { "type": "whole" }
            }
            """;

        var doc = RuleDocument.TryParse(json);

        Assert.NotNull(doc);
        Assert.Equal(2, doc!.Version);
        var shape = Assert.Single(doc.Shapes);
        var rect = Assert.IsType<RectGeom>(shape.Geom);
        Assert.Equal(1, rect.South);
    }

    [Fact]
    public void Validate_ExactlyMaxShapes_NotRejected()
    {
        // Boundary for the `> MaxShapes` cap: exactly MaxShapes shapes is allowed.
        var doc = new RuleDocument
        {
            Extent = new WholeExtent(),
            Shapes = Enumerable.Range(1, RuleDocument.MaxShapes)
                .Select(i => new ShapeDef { No = i, Kind = "zone", Geom = new RectGeom { South = 1, West = 2, North = 3, East = 4 } })
                .ToList(),
        };
        Assert.DoesNotContain(doc.Validate(), e => e.Contains("Too many shapes", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ExactlyMaxMembershipRules_NotRejected()
    {
        // Boundary for the `> MaxMembershipRules` cap: exactly MaxMembershipRules rules is allowed.
        var doc = new RuleDocument
        {
            Shapes = { new ShapeDef { No = 1, Kind = "zone", Geom = new RectGeom { South = 1, West = 2, North = 3, East = 4 } } },
            Extent = new WholeExtent(),
            Membership = Enumerable.Range(1, RuleDocument.MaxMembershipRules)
                .Select(_ => (MembershipRule)new PassesAnyRule { Zones = { 1 } }).ToList(),
        };
        Assert.DoesNotContain(doc.Validate(), e => e.Contains("Too many membership rules", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_TooManyMembershipRules_Rejected()
    {
        var doc = new RuleDocument
        {
            Shapes = { new ShapeDef { No = 1, Kind = "zone", Geom = new RectGeom { South = 1, West = 2, North = 3, East = 4 } } },
            Extent = new WholeExtent(),
            Membership = Enumerable.Range(1, RuleDocument.MaxMembershipRules + 1)
                .Select(_ => (MembershipRule)new PassesAnyRule { Zones = { 1 } }).ToList(),
        };
        Assert.Contains(doc.Validate(), e => e.Contains($"Too many membership rules (max {RuleDocument.MaxMembershipRules})", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_NullShapeElement_Reported()
    {
        var doc = RuleDocument.TryParse("""{ "version": 2, "shapes": [ null ], "extent": { "type": "whole" } }""");
        Assert.NotNull(doc);
        Assert.Contains(doc!.Validate(), e => e.Contains("has a null shape", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_NullMembershipElement_Reported()
    {
        const string json = """
            {
              "version": 2,
              "shapes": [ { "no": 1, "kind": "zone", "geom": { "type": "rect", "south": 1, "west": 2, "north": 3, "east": 4 } } ],
              "extent": { "type": "whole" },
              "membership": [ null ]
            }
            """;
        var doc = RuleDocument.TryParse(json);
        Assert.NotNull(doc);
        Assert.Contains(doc!.Validate(), e => e.Contains("has a null membership rule", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_NullTrackFilterElement_Reported()
    {
        const string json = """
            {
              "version": 2,
              "shapes": [ { "no": 1, "kind": "zone", "geom": { "type": "rect", "south": 1, "west": 2, "north": 3, "east": 4 } } ],
              "extent": { "type": "whole" },
              "trackFilters": [ null ]
            }
            """;
        var doc = RuleDocument.TryParse(json);
        Assert.NotNull(doc);
        Assert.Contains(doc!.Validate(), e => e.Contains("has a null track filter", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ZoneWithLineGeometry_Rejected()
    {
        // Kind ↔ geometry mirror of the existing gate-needs-a-line case: a zone must carry an area (rect).
        var doc = new RuleDocument
        {
            Shapes = { new ShapeDef { No = 1, Kind = "zone", Geom = new LineGeom { Lat1 = 0, Lng1 = 0, Lat2 = 1, Lng2 = 1 } } },
            Extent = new WholeExtent(),
        };
        Assert.Contains(doc.Validate(), e => e.Contains("zone needs an area geometry", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- gates (Phase 2)

    private static RuleDocument GateDoc() => new()
    {
        Shapes =
        {
            new ShapeDef { No = 1, Kind = "gate", Geom = new LineGeom { Lat1 = 47, Lng1 = 8, Lat2 = 47, Lng2 = 8.1, Direction = CrossDirection.Forward } },
            new ShapeDef { No = 2, Kind = "gate", Geom = new LineGeom { Lat1 = 47.2, Lng1 = 8, Lat2 = 47.2, Lng2 = 8.1 } },
        },
        Extent = new GatesExtent { StartGates = { 1 }, FinishGates = { 2 } },
        Membership = { new StartsAtGateRule { Zones = { 1 } } },
    };

    [Fact]
    public void GateDocument_RoundTrips_WithDirectionAsCamelCaseString()
    {
        var doc = GateDoc();
        var json = doc.Serialize();

        Assert.Contains("\"line\"", json, StringComparison.Ordinal);
        Assert.Contains("\"forward\"", json, StringComparison.Ordinal);          // enum serialised as camelCase string

        var parsed = RuleDocument.TryParse(json);
        Assert.NotNull(parsed);
        Assert.Empty(parsed!.Validate());
        Assert.Equal(json, parsed.Serialize());
        Assert.IsType<GatesExtent>(parsed.Extent);
        var line = Assert.IsType<LineGeom>(parsed.Shapes[0].Geom);
        Assert.Equal(CrossDirection.Forward, line.Direction);
    }

    [Fact]
    public void Validate_RejectsKindGeometryMismatch_AndWrongKindRefs()
    {
        var doc = new RuleDocument
        {
            Shapes =
            {
                new ShapeDef { No = 1, Kind = "zone", Geom = new RectGeom { South = 0, West = 0, North = 1, East = 1 } },
                new ShapeDef { No = 2, Kind = "gate", Geom = new RectGeom() },              // gate needs a line
            },
            Extent = new GatesExtent { StartGates = { 1 }, FinishGates = { 2 } },           // start gate 1 is a zone
            Membership = { new PassesAnyRule { Zones = { 2 } } },                           // passesAny points at a gate
        };

        var errors = doc.Validate();

        Assert.Contains(errors, e => e.Contains("gate needs a line geometry", StringComparison.Ordinal));
        Assert.Contains(errors, e => e.Contains("not a gate", StringComparison.Ordinal));       // gates extent → zone
        Assert.Contains(errors, e => e.Contains("not a zone", StringComparison.Ordinal));       // passesAny → gate
    }

    [Fact]
    public void Validate_GatesExtent_NeedsStartAndFinish()
    {
        var doc = new RuleDocument
        {
            Shapes = { new ShapeDef { No = 1, Kind = "gate", Geom = new LineGeom { Lat1 = 0, Lng1 = 0, Lat2 = 1, Lng2 = 1 } } },
            Extent = new GatesExtent { StartGates = { 1 } },   // no finish gate
        };

        Assert.Contains(doc.Validate(), e => e.Contains("at least one finish gate", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    [InlineData(RuleDocument.MaxGateSeconds + 1)]
    public void Validate_RejectsOutOfRangeGateDebounce(double seconds)
    {
        // The paint panel now sets this, so it is client input. NaN is the dangerous one: it makes every
        // `time - lastKept < debounce` test false, so nothing is ever debounced — and the mirror-image NaN
        // in MinEffortSeconds makes every span test false, so the segment matches nothing, silently.
        var doc = GateDoc() with { GateDebounceSeconds = seconds };

        Assert.Contains(doc.Validate(), e => e.Contains("GateDebounceSeconds must be between", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsOutOfRangeMinEffort_AndAcceptsTheBoundaries()
    {
        Assert.Contains(
            (GateDoc() with { MinEffortSeconds = double.NaN }).Validate(),
            e => e.Contains("MinEffortSeconds must be between", StringComparison.Ordinal));

        // 0 ("count every crossing") and the cap are both legitimate picks, not off-by-one rejections.
        Assert.Empty((GateDoc() with { GateDebounceSeconds = 0, MinEffortSeconds = 0 }).Validate());
        Assert.Empty((GateDoc() with { GateDebounceSeconds = RuleDocument.MaxGateSeconds }).Validate());
    }

    [Fact]
    public void GateDebounceSeconds_RoundTripsOverTheWire()
    {
        // The paint UI omits the field at the default and the server re-supplies it; a non-default must survive.
        var parsed = RuleDocument.TryParse((GateDoc() with { GateDebounceSeconds = 30 }).Serialize());
        Assert.NotNull(parsed);
        Assert.Equal(30, parsed!.GateDebounceSeconds);

        var omitted = RuleDocument.TryParse("""{"version":3,"shapes":[],"extent":{"type":"whole"},"membership":[]}""");
        Assert.Equal(15, omitted!.GateDebounceSeconds);
    }

    // ---------------------------------------------------------------- display geometry

    [Fact]
    public void GateLines_And_ZoneRects_PartitionShapesByGeometry()
    {
        var doc = new RuleDocument
        {
            Shapes =
            {
                new ShapeDef { No = 1, Kind = "zone", Geom = new RectGeom { South = 47.0, West = 8.0, North = 47.1, East = 8.1 } },
                new ShapeDef { No = 2, Kind = "gate", Geom = new LineGeom { Lat1 = 47.2, Lng1 = 8.2, Lat2 = 47.2, Lng2 = 8.3, Direction = CrossDirection.Forward } },
                new ShapeDef { No = 3, Kind = "gate", Geom = new LineGeom { Lat1 = 47.4, Lng1 = 8.4, Lat2 = 47.4, Lng2 = 8.5 } },
            },
            Extent = new GatesExtent { StartGates = { 2 }, FinishGates = { 3 } },
        };

        // ZoneRects surfaces only the rect; GateLines surfaces only the two gates (each was invisible to
        // the other before GateLines existed — a gate-only segment would draw nothing).
        var rect = Assert.Single(doc.ZoneRects());
        Assert.Equal(47.0, rect.South);

        var gates = doc.GateLines().ToList();
        Assert.Equal(2, gates.Count);
        Assert.Equal(8.2, gates[0].Lng1);
        Assert.Equal(CrossDirection.Forward, gates[0].Direction);
    }

    // ---------------------------------------------------------------- endpointInZone

    [Fact]
    public void EndpointInZone_RoundTrips_WithEndpointAsCamelCaseString()
    {
        var doc = new RuleDocument
        {
            Shapes = { new ShapeDef { No = 1, Kind = "zone", Geom = new RectGeom { South = 0, West = 0, North = 1, East = 1 } } },
            Extent = new WholeExtent(),
            Membership = { new EndpointInZoneRule { Endpoint = Endpoint.Either, Zones = { 1 } } },
        };
        var json = doc.Serialize();

        Assert.Contains("\"endpointInZone\"", json, StringComparison.Ordinal);
        Assert.Contains("\"either\"", json, StringComparison.Ordinal);            // enum serialised as camelCase string

        var parsed = RuleDocument.TryParse(json);
        Assert.NotNull(parsed);
        Assert.Empty(parsed!.Validate());
        Assert.Equal(json, parsed.Serialize());
        var rule = Assert.IsType<EndpointInZoneRule>(Assert.Single(parsed.Membership));
        Assert.Equal(Endpoint.Either, rule.Endpoint);
    }

    [Fact]
    public void EndpointInZone_DefaultsToStart_WhenEndpointOmitted()
    {
        const string json = """
            {
              "version": 2,
              "shapes": [ { "no": 1, "kind": "zone", "geom": { "type": "rect", "south": 0, "west": 0, "north": 1, "east": 1 } } ],
              "extent": { "type": "whole" },
              "membership": [ { "type": "endpointInZone", "zones": [1] } ]
            }
            """;

        var doc = RuleDocument.TryParse(json);
        Assert.NotNull(doc);
        Assert.Empty(doc!.Validate());
        var rule = Assert.IsType<EndpointInZoneRule>(Assert.Single(doc.Membership));
        Assert.Equal(Endpoint.Start, rule.Endpoint);
    }

    [Fact]
    public void EndpointInZone_RejectsGateRef()
    {
        var doc = new RuleDocument
        {
            Shapes = { new ShapeDef { No = 1, Kind = "gate", Geom = new LineGeom { Lat1 = 0, Lng1 = 0, Lat2 = 1, Lng2 = 1 } } },
            Extent = new WholeExtent(),
            Membership = { new EndpointInZoneRule { Endpoint = Endpoint.Start, Zones = { 1 } } },  // points at a gate
        };

        Assert.Contains(doc.Validate(), e => e.Contains("not a zone", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- track filters (Phase 4)

    private static RuleDocument WithFilters(params TrackFilter[] filters)
    {
        var doc = Doc();
        return doc with { TrackFilters = filters.ToList() };
    }

    [Fact]
    public void TrackFilters_RoundTrip_AllThreeTypes()
    {
        var doc = WithFilters(
            new DateRangeFilter { From = "2025-01-01", To = "2025-12-31" },
            new ActivityTypeFilter { Types = { "Run", "TrailRun" } },
            new OwnerFilter { UserIds = { "me-id", "friend-id" } });

        var parsed = RuleDocument.TryParse(doc.Serialize());

        Assert.NotNull(parsed);
        Assert.Equal(doc.Serialize(), parsed!.Serialize());
        Assert.Empty(parsed.Validate());
        Assert.IsType<DateRangeFilter>(parsed.TrackFilters[0]);
        Assert.IsType<ActivityTypeFilter>(parsed.TrackFilters[1]);
        Assert.IsType<OwnerFilter>(parsed.TrackFilters[2]);
    }

    [Fact]
    public void Parse_OldDocWithoutTrackFilters_ParsesAndValidates()
    {
        const string json = """
            {
              "version": 2,
              "shapes": [ { "no": 1, "kind": "zone", "geom": { "type": "rect", "south": 1, "west": 2, "north": 3, "east": 4 } } ],
              "extent": { "type": "whole" },
              "membership": [ { "zones": [1], "type": "staysWithin" } ]
            }
            """;

        var doc = RuleDocument.TryParse(json);

        Assert.NotNull(doc);
        Assert.Empty(doc!.TrackFilters);
        Assert.Empty(doc.Validate());
    }

    [Fact]
    public void TrackFilters_EmptyOwnerUserIds_IsValid()
    {
        var errors = WithFilters(new OwnerFilter()).Validate();
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData("2025-13-01", null)]      // bad month
    [InlineData("not-a-date", null)]      // unparseable
    public void Validate_DateRange_RejectsUnparseable(string? from, string? to)
    {
        var errors = WithFilters(new DateRangeFilter { From = from, To = to }).Validate();
        Assert.Contains(errors, e => e.Contains("unparseable", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_DateRange_RejectsFromAfterTo()
    {
        var errors = WithFilters(new DateRangeFilter { From = "2025-12-31", To = "2025-01-01" }).Validate();
        Assert.Contains(errors, e => e.Contains("from is after to", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_DateRange_RejectsNoBounds()
    {
        var errors = WithFilters(new DateRangeFilter()).Validate();
        Assert.Contains(errors, e => e.Contains("at least one of from / to", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ActivityType_RejectsEmptyAndBlank()
    {
        Assert.Contains(WithFilters(new ActivityTypeFilter()).Validate(), e => e.Contains("at least one type", StringComparison.Ordinal));
        Assert.Contains(WithFilters(new ActivityTypeFilter { Types = { " " } }).Validate(), e => e.Contains("blank type", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_RejectsDuplicateFilterType()
    {
        var errors = WithFilters(
            new DateRangeFilter { From = "2025-01-01" },
            new DateRangeFilter { To = "2025-12-31" }).Validate();
        Assert.Contains(errors, e => e.Contains("Duplicate track filter", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_UnknownTrackFilterType_ReturnsNull()
    {
        const string json = """
            {
              "version": 2,
              "shapes": [ { "no": 1, "kind": "zone", "geom": { "type": "rect", "south": 1, "west": 2, "north": 3, "east": 4 } } ],
              "extent": { "type": "whole" },
              "membership": [ { "zones": [1], "type": "staysWithin" } ],
              "trackFilters": [ { "type": "altitude" } ]
            }
            """;

        Assert.Null(RuleDocument.TryParse(json));
    }

    // ---------------------------------------------------------------- gridFilters (v3, Phase 1)

    [Fact]
    public void GridFilters_RoundTrip_AllFieldShapes()
    {
        var doc = WithFilters(new GridFiltersFilter
        {
            Filters =
            {
                ["name"] = new ColumnFilter { Text = "*morning*" },
                ["type"] = new ColumnFilter { In = { "Run", "TrailRun" } },
                ["distance"] = new ColumnFilter { Range = "10-20" },
                ["avghr"] = new ColumnFilter { Min = "120", Max = "160" },
                ["date"] = new ColumnFilter { From = "2025-06-01", To = "2025-06-30" },
            },
        });

        var json = doc.Serialize();
        var parsed = RuleDocument.TryParse(json);

        Assert.NotNull(parsed);
        Assert.Equal(json, parsed!.Serialize());
        Assert.Empty(parsed.Validate());

        // Derived IsEmpty must never leak into the stored document.
        Assert.DoesNotContain("isEmpty", json, System.StringComparison.OrdinalIgnoreCase);

        var grid = Assert.IsType<GridFiltersFilter>(Assert.Single(parsed.TrackFilters));
        Assert.Equal("*morning*", grid.Filters["name"].Text);
        Assert.Equal(new List<string> { "Run", "TrailRun" }, grid.Filters["type"].In);
        Assert.Equal("10-20", grid.Filters["distance"].Range);
        Assert.Equal("120", grid.Filters["avghr"].Min);
        Assert.Equal("2025-06-01", grid.Filters["date"].From);
    }

    /// <summary>The v3 companion to <see cref="Parse_TsAuthoredDocument_AllFieldsSurvive"/>: mirrors what the
    /// P3 paint UI emits (adopted grid filters under the <c>gridFilters</c> discriminator). Drift → fix BOTH sides.</summary>
    [Fact]
    public void Parse_TsAuthoredV3Document_GridFiltersSurvive()
    {
        const string json = """
            {
              "version": 3,
              "shapes": [ { "no": 1, "kind": "zone", "geom": { "type": "rect", "south": 47.0, "west": 8.0, "north": 47.1, "east": 8.1 } } ],
              "extent": { "type": "whole" },
              "membership": [ { "type": "passesAll", "zones": [1] } ],
              "trackFilters": [
                { "type": "gridFilters", "filters": {
                    "distance": { "range": "10-20" },
                    "name": { "text": "*morning*" },
                    "type": { "in": ["Run", "TrailRun"] },
                    "date": { "from": "2025-06-01", "to": "2025-06-30" }
                } }
              ]
            }
            """;

        var doc = RuleDocument.TryParse(json);

        Assert.NotNull(doc);
        Assert.Empty(doc!.Validate());
        var grid = Assert.IsType<GridFiltersFilter>(Assert.Single(doc.TrackFilters));
        Assert.Equal("10-20", grid.Filters["distance"].Range);
        Assert.Equal("*morning*", grid.Filters["name"].Text);
        Assert.Equal(new List<string> { "Run", "TrailRun" }, grid.Filters["type"].In);
        Assert.Equal("2025-06-30", grid.Filters["date"].To);
    }

    [Fact]
    public void Validate_GridFilters_RejectsAllEmpty()
    {
        Assert.Contains(
            WithFilters(new GridFiltersFilter()).Validate(),
            e => e.Contains("non-empty column filter", StringComparison.Ordinal));
        Assert.Contains(
            WithFilters(new GridFiltersFilter { Filters = { ["distance"] = new ColumnFilter() } }).Validate(),
            e => e.Contains("non-empty column filter", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- version gate (Phase 1)

    [Fact]
    public void Parse_V2Document_StillValidates()
    {
        // Cached pre-deploy clients keep POSTing v2 after the v3 rollout — must parse and match forever.
        const string json = """
            {
              "version": 2,
              "shapes": [ { "no": 1, "kind": "zone", "geom": { "type": "rect", "south": 1, "west": 2, "north": 3, "east": 4 } } ],
              "extent": { "type": "whole" },
              "membership": [ { "zones": [1], "type": "staysWithin" } ],
              "trackFilters": [ { "type": "dateRange", "from": "2025-01-01" } ]
            }
            """;

        var doc = RuleDocument.TryParse(json);
        Assert.NotNull(doc);
        Assert.Empty(doc!.Validate());
    }

    [Theory]
    [InlineData(1)]   // below MinSupportedVersion
    [InlineData(4)]   // above CurrentVersion
    public void Validate_RejectsOutOfRangeVersion(int version)
    {
        var doc = Doc() with { Version = version };
        Assert.Contains(doc.Validate(), e => e.Contains($"version {version}", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- SC-03: malformed → 400, never 500

    // A missing polymorphic "type" discriminator throws NotSupportedException (not JsonException) inside
    // STJ — TryParse must swallow it as "malformed" rather than let a 500 escape the controller.
    [Fact]
    public void TryParse_MissingDiscriminator_ReturnsNull()
    {
        Assert.Null(RuleDocument.TryParse("""{ "shapes": [], "extent": {} }"""));
    }

    // Explicit JSON nulls deserialize into the non-nullable-annotated sections; Validate must return
    // errors (→ 400) instead of NRE-ing (→ 500).
    [Theory]
    [InlineData("""{ "version": 2, "shapes": null, "extent": { "type": "whole" } }""")]
    [InlineData("""{ "version": 2, "shapes": [], "extent": null }""")]
    [InlineData("""{ "version": 2, "shapes": [], "extent": { "type": "whole" }, "membership": null }""")]
    [InlineData("""{ "version": 2, "shapes": [], "extent": { "type": "whole" }, "trackFilters": null }""")]
    public void Validate_NullSections_ReturnsErrors_NoThrow(string json)
    {
        var doc = RuleDocument.TryParse(json);
        Assert.NotNull(doc);
        var errors = doc!.Validate();     // must not throw
        Assert.Contains(errors, e => e.Contains("null sections", StringComparison.Ordinal));
    }

    // An explicit null "kind" on a shape must report the unsupported-kind error, not NRE.
    [Fact]
    public void Validate_NullKind_ReportsUnsupportedKind()
    {
        const string json = """
            {
              "version": 2,
              "shapes": [ { "no": 1, "kind": null, "geom": { "type": "rect", "south": 1, "west": 2, "north": 3, "east": 4 } } ],
              "extent": { "type": "whole" }
            }
            """;
        var doc = RuleDocument.TryParse(json);
        Assert.NotNull(doc);
        Assert.Contains(doc!.Validate(), e => e.Contains("unsupported kind", StringComparison.Ordinal));
    }

    // ---------------------------------------------------------------- SC-04: size caps

    [Fact]
    public void Validate_TooManyShapes_Rejected()
    {
        var doc = new RuleDocument
        {
            Extent = new WholeExtent(),
            Shapes = Enumerable.Range(1, RuleDocument.MaxShapes + 1)
                .Select(i => new ShapeDef { No = i, Kind = "zone",
                    Geom = new RectGeom { South = 1, West = 2, North = 3, East = 4 } })
                .ToList(),
        };
        Assert.Contains(doc.Validate(), e => e.Contains($"max {RuleDocument.MaxShapes}", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_OversizedInList_Rejected()
    {
        var filter = new GridFiltersFilter
        {
            Filters = { ["type"] = new ColumnFilter
            {
                In = Enumerable.Range(0, RuleDocument.MaxInValues + 1).Select(i => $"t{i}").ToList()
            } },
        };
        Assert.Contains(WithFilters(filter).Validate(), e => e.Contains($"max {RuleDocument.MaxInValues}", StringComparison.Ordinal));
    }

    // An explicit "in": null in a gridFilters column deserializes to a null list — IsEmpty must treat it
    // as empty (not NRE on In.Count) so the filter validates as empty rather than crashing.
    [Fact]
    public void ColumnFilter_IsEmpty_NullIn_True()
    {
        var doc = RuleDocument.TryParse("""
            {
              "version": 3,
              "shapes": [ { "no": 1, "kind": "zone", "geom": { "type": "rect", "south": 1, "west": 2, "north": 3, "east": 4 } } ],
              "extent": { "type": "whole" },
              "trackFilters": [ { "type": "gridFilters", "filters": { "type": { "in": null } } } ]
            }
            """);
        Assert.NotNull(doc);
        // The gridFilters filter's only column has a null In → treated empty → "needs at least one
        // non-empty column filter" error, no exception.
        Assert.Contains(doc!.Validate(), e => e.Contains("non-empty column filter", StringComparison.Ordinal));
    }
}
