using System.Text.Json;
using System.Text.Json.Serialization;
using RouteBaseWeb.Models.Geometry;
using RouteBaseWeb.Models.GridFilter;

namespace RouteBaseWeb.Services.Segments.Rules;

/// <summary>
/// The versioned rule document that fully defines a shape segment. Stored verbatim in
/// <c>Segment.match_rules_json</c> and exchanged verbatim with the paint UI, so adding a
/// new rule type is a new derived record — no schema migration, no extra endpoint work.
///
/// Semantics: shapes are pure numbered geometry; the extent rule turns a track into effort
/// spans; membership rules are AND'd and evaluated per span.
/// </summary>
public sealed record RuleDocument
{
    public const int CurrentVersion = 3;

    /// <summary>Oldest doc version still accepted. v2 (date/type/owner track filters, no gridFilters) is
    /// parsed and matched forever; cached pre-deploy clients still POST v2. Re-save lazily upgrades to v3.</summary>
    public const int MinSupportedVersion = 2;

    public int Version { get; init; } = CurrentVersion;
    public List<ShapeDef> Shapes { get; init; } = new();
    public ExtentRule Extent { get; init; } = new WholeExtent();
    public List<MembershipRule> Membership { get; init; } = new();

    /// <summary>Non-spatial track filters (date range / activity type / owner), AND'd as SQL predicates
    /// during candidate loading — before the bbox/GPS geometry stages. Optional; old docs omit it.</summary>
    public List<TrackFilter> TrackFilters { get; init; } = new();

    /// <summary>Same-gate crossings closer than this are collapsed (a track lingering on the line
    /// must not read as several efforts). Document constant, not surfaced in the UI.</summary>
    public double GateDebounceSeconds { get; init; } = 15;

    /// <summary>A paired gate span shorter than this is discarded (a stray start-then-finish blip).
    /// Document constant, not surfaced in the UI. 0 = keep every span.</summary>
    public double MinEffortSeconds { get; init; } = 0;

    // SC-04 size caps (~100× real UI usage; implementer-tunable, never removed). Bound worst-case Stage-2
    // CPU (shapes × samples per candidate track) and SQL parameter counts (in-lists → placeholders past
    // SQLITE_MAX_VARIABLE_NUMBER throw). Complements SB-01's candidate-count cap (this caps the multiplier).
    public const int MaxShapes = 200;
    public const int MaxMembershipRules = 100;
    public const int MaxZonesPerRule = 100;
    public const int MaxActivityTypes = 200;
    public const int MaxInValues = 500;
    public const int MaxFilterTextLength = 2000;

    /// <summary>Upper bound for the two gate timing constants. An hour is far past any real lap; the cap
    /// exists so a hand-authored absurd value is a 400 rather than a segment that silently matches nothing.</summary>
    public const double MaxGateSeconds = 3600;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        AllowOutOfOrderMetadataProperties = true,   // "type" discriminator may appear anywhere
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    /// <summary>Parse a stored/received document. Returns null when json is null/blank or malformed.</summary>
    public static RuleDocument? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            return JsonSerializer.Deserialize<RuleDocument>(json, JsonOpts);
        }
        // SC-03: a missing "type" discriminator on a polymorphic field throws NotSupportedException, not
        // JsonException — treat both as "malformed" (→ 400) instead of letting it escape as a 500.
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    public string Serialize() => JsonSerializer.Serialize(this, JsonOpts);

    /// <summary>
    /// The zone rectangles for display consumers (segments-index preview, Statistics map).
    /// Keeps the pre-rework wire shape so those readers stay geometry-only.
    /// </summary>
    public IEnumerable<RectGeom> ZoneRects() =>
        Shapes.Select(s => s.Geom).OfType<RectGeom>();

    /// <summary>
    /// The gate lines for display consumers (segments-index preview outline). Gate segments carry no
    /// zones, so <see cref="ZoneRects"/> alone leaves them with nothing to draw when they have no
    /// matched efforts yet — this surfaces the crossing lines as the fallback outline.
    /// </summary>
    public IEnumerable<LineGeom> GateLines() =>
        Shapes.Select(s => s.Geom).OfType<LineGeom>();

    /// <summary>
    /// Structural validation; returns human-readable errors (empty = valid). Geometry range
    /// checks live here too so every entry point (create/update/preview) shares them.
    /// </summary>
    public List<string> Validate()
    {
        var errors = new List<string>();

        // SC-03: explicit JSON nulls deserialize into these non-nullable-annotated properties (STJ assigns
        // null), so guard before dereferencing — a null section is malformed (→ 400), not a 500.
        if (Shapes is null || Extent is null || Membership is null || TrackFilters is null)
        {
            errors.Add("Rules document has null sections");
            return errors;
        }

        if (Version < MinSupportedVersion || Version > CurrentVersion)
            errors.Add($"Unsupported rules version {Version} (supported {MinSupportedVersion}–{CurrentVersion})");

        if (Shapes.Count == 0)
            errors.Add("At least one shape is required");
        if (Shapes.Count > MaxShapes)
            errors.Add($"Too many shapes (max {MaxShapes})");
        if (Membership.Count > MaxMembershipRules)
            errors.Add($"Too many membership rules (max {MaxMembershipRules})");

        // The gate timing constants are now client-settable (the paint panel surfaces the debounce), and
        // both feed bare comparisons in the extractor/extent rules. A NaN there is silently absorbing: every
        // `< debounce` and `>= minEffort` test is false, so the segment matches nothing with no error anywhere.
        foreach (var (name, value) in new[] { (nameof(GateDebounceSeconds), GateDebounceSeconds), (nameof(MinEffortSeconds), MinEffortSeconds) })
            if (!double.IsFinite(value) || value < 0 || value > MaxGateSeconds)
                errors.Add($"{name} must be between 0 and {MaxGateSeconds} seconds");

        var numbers = new HashSet<int>();
        var kinds = new Dictionary<int, string>();
        foreach (var s in Shapes)
        {
            if (s is null) { errors.Add("Rules document has a null shape"); continue; }
#pragma warning disable CA1308 // normalized to lowercase to match the lowercase ShapeKinds constants
            var kind = s.Kind?.ToLowerInvariant() ?? "";
#pragma warning restore CA1308
            if (!numbers.Add(s.No)) errors.Add($"Duplicate shape number {s.No}");
            else kinds[s.No] = kind;

            if (kind is not (ShapeKinds.Zone or ShapeKinds.Gate))
                errors.Add($"Shape {s.No}: unsupported kind \"{s.Kind}\"");
            if (s.Geom == null) { errors.Add($"Shape {s.No} has no geometry"); continue; }

            // Kind ↔ geometry must agree: zones are areas (rect), gates are lines.
            if (kind == ShapeKinds.Zone && s.Geom is not RectGeom)
                errors.Add($"Shape {s.No}: zone needs an area geometry");
            if (kind == ShapeKinds.Gate && s.Geom is not LineGeom)
                errors.Add($"Shape {s.No}: gate needs a line geometry");
            errors.AddRange(s.Geom.Validate(s.No));
        }

        var catalog = new ShapeCatalog(numbers, kinds);
        foreach (var err in Extent.Validate(catalog)) errors.Add(err);
        foreach (var rule in Membership)
        {
            if (rule is null) { errors.Add("Rules document has a null membership rule"); continue; }
            foreach (var err in rule.Validate(catalog)) errors.Add(err);
        }

        // Track filters: at most one per type (a hand-authored doc with two dateRanges is ambiguous).
        var seenFilterTypes = new HashSet<Type>();
        foreach (var f in TrackFilters)
        {
            if (f is null) { errors.Add("Rules document has a null track filter"); continue; }
            if (!seenFilterTypes.Add(f.GetType()))
                errors.Add($"Duplicate track filter of type \"{f.Discriminator}\"");
            foreach (var err in f.Validate()) errors.Add(err);
        }

        return errors;
    }
}

/// <summary>The shape numbers and their kinds, threaded to every rule's <c>Validate</c> so rules
/// can reject dangling refs and wrong-kind refs (a zone rule pointing at a gate, etc.).</summary>
public sealed record ShapeCatalog(IReadOnlySet<int> Numbers, IReadOnlyDictionary<int, string> Kinds)
{
    public bool IsZone(int no) => Kinds.TryGetValue(no, out var k) && k == ShapeKinds.Zone;
    public bool IsGate(int no) => Kinds.TryGetValue(no, out var k) && k == ShapeKinds.Gate;
}

/// <summary>The ShapeDef.Kind vocabulary (D2): "zone" (area) or "gate" (directed line).</summary>
public static class ShapeKinds
{
    public const string Zone = "zone";
    public const string Gate = "gate";
}

/// <summary>A numbered shape: pure geometry, no semantics. Rules reference it by <see cref="No"/>.</summary>
public sealed record ShapeDef
{
    public int No { get; init; }

    /// <summary>"zone" (area) or "gate" (directed line).</summary>
    public string Kind { get; init; } = ShapeKinds.Zone;

    public ShapeGeom? Geom { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(RectGeom), "rect")]
[JsonDerivedType(typeof(LineGeom), "line")]
public abstract record ShapeGeom
{
    public abstract IEnumerable<string> Validate(int shapeNo);
}

public sealed record RectGeom : ShapeGeom
{
    public double South { get; init; }
    public double West { get; init; }
    public double North { get; init; }
    public double East { get; init; }

    public IShape ToShape() => new GeoRect(South, North, West, East);

    public override IEnumerable<string> Validate(int shapeNo)
    {
        if (South is < -90 or > 90 || North is < -90 or > 90 ||
            West is < -180 or > 180 || East is < -180 or > 180)
            yield return $"Shape {shapeNo}: bounds out of range (lat [-90,90], lng [-180,180])";
    }
}

/// <summary>A directed line (gate). Efforts are defined by crossing it; <see cref="Direction"/>
/// optionally restricts which way.</summary>
public sealed record LineGeom : ShapeGeom
{
    public double Lat1 { get; init; }
    public double Lng1 { get; init; }
    public double Lat2 { get; init; }
    public double Lng2 { get; init; }
    public CrossDirection Direction { get; init; } = CrossDirection.Any;

    public GeoLine ToLine() => new(Lat1, Lng1, Lat2, Lng2, Direction);

    public override IEnumerable<string> Validate(int shapeNo)
    {
        if (Lat1 is < -90 or > 90 || Lat2 is < -90 or > 90 ||
            Lng1 is < -180 or > 180 || Lng2 is < -180 or > 180)
            yield return $"Shape {shapeNo}: gate endpoints out of range (lat [-90,90], lng [-180,180])";
        if (Lat1 == Lat2 && Lng1 == Lng2)
            yield return $"Shape {shapeNo}: gate is a single point";
    }
}

// ------------------------------------------------------------------ extent rules

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(WholeExtent), "whole")]
[JsonDerivedType(typeof(ZoneSpanExtent), "zoneSpan")]
[JsonDerivedType(typeof(GatesExtent), "gates")]
public abstract record ExtentRule
{
    public abstract IEnumerable<string> Validate(ShapeCatalog catalog);
}

/// <summary>The entire track is one effort span.</summary>
public sealed record WholeExtent : ExtentRule
{
    public override IEnumerable<string> Validate(ShapeCatalog catalog) => [];
}

/// <summary>One effort span: first to last sample inside any of the referenced zones.</summary>
public sealed record ZoneSpanExtent : ExtentRule
{
    public List<int> Zones { get; init; } = new();

    public override IEnumerable<string> Validate(ShapeCatalog catalog)
    {
        if (Zones.Count == 0) yield return "zoneSpan extent needs at least one zone";
        foreach (var z in Zones)
        {
            if (!catalog.Numbers.Contains(z)) yield return $"zoneSpan extent references unknown shape {z}";
            else if (!catalog.IsZone(z)) yield return $"zoneSpan extent references shape {z} which is not a zone";
        }
    }
}

/// <summary>Efforts run from a start-gate crossing to the next finish-gate crossing (greedy,
/// non-overlapping). A gate may be listed as both, defining a lap course.</summary>
public sealed record GatesExtent : ExtentRule
{
    public List<int> StartGates { get; init; } = new();
    public List<int> FinishGates { get; init; } = new();

    public override IEnumerable<string> Validate(ShapeCatalog catalog)
    {
        if (StartGates.Count == 0) yield return "gates extent needs at least one start gate";
        if (FinishGates.Count == 0) yield return "gates extent needs at least one finish gate";
        foreach (var g in StartGates.Concat(FinishGates))
        {
            if (!catalog.Numbers.Contains(g)) yield return $"gates extent references unknown shape {g}";
            else if (!catalog.IsGate(g)) yield return $"gates extent references shape {g} which is not a gate";
        }
    }
}

// ------------------------------------------------------------------ membership rules

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(PassesAnyRule), "passesAny")]
[JsonDerivedType(typeof(PassesAllRule), "passesAll")]
[JsonDerivedType(typeof(StaysWithinRule), "staysWithin")]
[JsonDerivedType(typeof(AvoidsRule), "avoids")]
[JsonDerivedType(typeof(StartsAtGateRule), "startsAtGate")]
[JsonDerivedType(typeof(EndsAtGateRule), "endsAtGate")]
[JsonDerivedType(typeof(EndpointInZoneRule), "endpointInZone")]
public abstract record MembershipRule
{
    /// <summary>Referenced shape numbers — zones for the spatial rules, gates for the gate rules.</summary>
    public List<int> Zones { get; init; } = new();

    /// <summary>Gate rules reference gates; the spatial rules reference zones. Drives kind validation.</summary>
    protected virtual bool WantsGate => false;

    public virtual IEnumerable<string> Validate(ShapeCatalog catalog)
    {
        var name = GetType().Name;
        var noun = WantsGate ? ShapeKinds.Gate : ShapeKinds.Zone;
        if (Zones.Count == 0) yield return $"{name}: needs at least one {noun}";
        if (Zones.Count > RuleDocument.MaxZonesPerRule)
            yield return $"{name}: too many {noun}s (max {RuleDocument.MaxZonesPerRule})";
        foreach (var z in Zones)
        {
            if (!catalog.Numbers.Contains(z)) yield return $"{name}: references unknown shape {z}";
            else if (WantsGate ? !catalog.IsGate(z) : !catalog.IsZone(z))
                yield return $"{name}: shape {z} is not a {noun}";
        }
    }
}

/// <summary>Effort touches at least one of the zones (OR). <see cref="Group"/> is UI metadata only.</summary>
public sealed record PassesAnyRule : MembershipRule
{
    public int Group { get; init; }
}

/// <summary>Effort touches every listed zone (AND).</summary>
public sealed record PassesAllRule : MembershipRule;

/// <summary>Every sample of the effort lies inside the union of the listed zones.</summary>
public sealed record StaysWithinRule : MembershipRule;

/// <summary>Effort touches none of the listed zones.</summary>
public sealed record AvoidsRule : MembershipRule;

/// <summary>The effort opens on a crossing of one of the listed gates (its first segment).</summary>
public sealed record StartsAtGateRule : MembershipRule
{
    protected override bool WantsGate => true;
}

/// <summary>The effort closes on a crossing of one of the listed gates (its last segment).</summary>
public sealed record EndsAtGateRule : MembershipRule
{
    protected override bool WantsGate => true;
}

/// <summary>Which end of the effort a zone-endpoint rule anchors. <see cref="Either"/> = start OR finish.
/// The camelCase-string wire form is declared ON THE TYPE (TimeMode pattern) so the OpenAPI generator
/// emits the string enum; <see cref="RuleDocument"/>'s options-level converter already produced the
/// same strings, so this is wire-neutral.</summary>
[JsonConverter(typeof(EndpointJsonConverter))]
public enum Endpoint { Start, Finish, Either }

/// <summary>Pins Endpoint's wire form to "start"/"finish"/"either" under ANY JsonSerializerOptions.</summary>
public sealed class EndpointJsonConverter : JsonStringEnumConverter<Endpoint>
{
    public EndpointJsonConverter() : base(JsonNamingPolicy.CamelCase) { }
}

/// <summary>The effort's first/last sample lies inside one of the listed zones (OR across zones) —
/// the zone analogue of <see cref="StartsAtGateRule"/>/<see cref="EndsAtGateRule"/>. Membership only:
/// it filters which tracks qualify, it does not define the effort extent.</summary>
public sealed record EndpointInZoneRule : MembershipRule
{
    public Endpoint Endpoint { get; init; } = Endpoint.Start;
}

// ------------------------------------------------------------------ track filters

/// <summary>
/// A non-spatial predicate on the candidate activity itself (date / type / owner), applied as SQL
/// during candidate loading. SQL-free here — the matcher-side <c>CandidateSqlFilters</c> turns these
/// into parameterised WHERE clauses, keeping this rules core storage-agnostic.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(DateRangeFilter), "dateRange")]
[JsonDerivedType(typeof(ActivityTypeFilter), "activityType")]
[JsonDerivedType(typeof(OwnerFilter), "owner")]
[JsonDerivedType(typeof(GridFiltersFilter), "gridFilters")]
public abstract record TrackFilter
{
    /// <summary>The discriminator string, for duplicate-type error messages.</summary>
    public abstract string Discriminator { get; }

    public abstract IEnumerable<string> Validate();
}

/// <summary>Local start date is within [From, To] (either bound optional; end-day inclusive).
/// Dates are "yyyy-MM-dd" strings compared against <c>start_datetime_loc</c>.</summary>
public sealed record DateRangeFilter : TrackFilter
{
    public string? From { get; init; }
    public string? To { get; init; }

    public override string Discriminator => "dateRange";

    public bool TryParseFrom(out DateOnly value) => TryParseDate(From, out value);
    public bool TryParseTo(out DateOnly value) => TryParseDate(To, out value);

    private static bool TryParseDate(string? s, out DateOnly value) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out value);

    public override IEnumerable<string> Validate()
    {
        bool hasFrom = !string.IsNullOrWhiteSpace(From);
        bool hasTo   = !string.IsNullOrWhiteSpace(To);
        if (!hasFrom && !hasTo)
        {
            yield return "dateRange filter needs at least one of from / to";
            yield break;
        }

        bool fromOk = !hasFrom || TryParseFrom(out _);
        bool toOk   = !hasTo   || TryParseTo(out _);
        if (hasFrom && !fromOk) yield return $"dateRange filter: unparseable from \"{From}\" (expected yyyy-MM-dd)";
        if (hasTo   && !toOk)   yield return $"dateRange filter: unparseable to \"{To}\" (expected yyyy-MM-dd)";

        if (fromOk && toOk && hasFrom && hasTo &&
            TryParseFrom(out var f) && TryParseTo(out var t) && f > t)
            yield return "dateRange filter: from is after to";
    }
}

/// <summary>Activity type is one of the listed raw types (exact multi-select).</summary>
public sealed record ActivityTypeFilter : TrackFilter
{
    public List<string> Types { get; init; } = new();

    public override string Discriminator => "activityType";

    public override IEnumerable<string> Validate()
    {
        if (Types is null || Types.Count == 0) { yield return "activityType filter needs at least one type"; yield break; }
        if (Types.Count > RuleDocument.MaxActivityTypes)
            yield return $"activityType filter has too many types (max {RuleDocument.MaxActivityTypes})";
        if (Types.Any(string.IsNullOrWhiteSpace)) yield return "activityType filter has a blank type";
    }
}

/// <summary>Which owners' visible tracks match. Server-authored (resolved user ids); an empty list
/// matches nobody. See <c>CandidateSqlFilters</c> for the owner-precedence + visibility handling.</summary>
public sealed record OwnerFilter : TrackFilter
{
    public List<string> UserIds { get; init; } = new();

    public override string Discriminator => "owner";

    // Empty userIds is valid (= match nobody); no other structural constraint.
    public override IEnumerable<string> Validate() => [];
}

/// <summary>The v3 filter: activities-table column filters adopted verbatim as live rules, keyed by grid
/// column key (distance/name/type/…). Reuses the wire <see cref="ColumnFilter"/> so there is no third
/// mirror. The matcher (<c>GridFilterSqlTranslator</c>, P2) renders each entry to a SQL predicate via the
/// column registry allowlist — unknown / non-filterable keys are ignored there, never here.</summary>
public sealed record GridFiltersFilter : TrackFilter
{
    public Dictionary<string, ColumnFilter> Filters { get; init; } = new(StringComparer.Ordinal);

    public override string Discriminator => "gridFilters";

    public override IEnumerable<string> Validate()
    {
        // An all-empty map carries no constraint — a hand-authored doc with one shouldn't validate.
        if (Filters is null || Filters.Count == 0 || Filters.Values.All(f => f is null || f.IsEmpty))
        {
            yield return "gridFilters filter needs at least one non-empty column filter";
            yield break;
        }

        // SC-04: bound per-column sizes so an in-list can't blow past SQLITE_MAX_VARIABLE_NUMBER and a
        // range/text term can't drive an unbounded LIKE/parse.
        foreach (var (key, f) in Filters)
        {
            if (f is null) continue;
            if (f.In is { Count: > RuleDocument.MaxInValues })
                yield return $"gridFilters \"{key}\": too many values (max {RuleDocument.MaxInValues})";
            if (f.Range is { Length: > RuleDocument.MaxFilterTextLength })
                yield return $"gridFilters \"{key}\": range too long (max {RuleDocument.MaxFilterTextLength})";
            if (f.Text is { Length: > RuleDocument.MaxFilterTextLength })
                yield return $"gridFilters \"{key}\": text too long (max {RuleDocument.MaxFilterTextLength})";
        }
    }
}
