using System.Text.Json;
using Xunit;

namespace RouteBaseWeb.Tests.Architecture;

/// <summary>
/// Wire-ambiguity gates (AR-03 B1-B2) over the committed OpenAPI document.
///
/// B1: one property NAME must not mean an instant in one schema and something else in another —
/// <c>startedAt</c> was a date-time in 8 schemas and a wall-clock string in TrashedActivity, the
/// only member of its class (E2 renames it StartedIso and empties the pin). Dismissed conflicts,
/// recorded by name so the narrow scope reads as deliberate: <c>ascentMeters</c>/<c>altitude</c>/
/// <c>seq</c> are same-name-different-WIDTH and follow storage; <c>source</c>/<c>from</c>/<c>to</c>/
/// <c>owner</c> name unrelated concepts in unrelated DTOs. Widening past date-time-vs-not would
/// need a per-name judgement call — the moment a gate stops meaning anything.
///
/// B2: a component schema with no type/$ref/properties/enum/allOf/oneOf/anyOf renders as TS
/// <c>unknown</c> — the class-level twin of the property-level trap AR-02 hit
/// (CustomConvertedTimestampSchemaTransformer). E1's OpaqueSchemaTransformer empties this pin.
/// </summary>
public class WireTypeAmbiguityRatchetTests
{
    private static readonly IReadOnlySet<string> AmbiguousNamePin =
        new HashSet<string>(StringComparer.Ordinal);   // E2 renamed the wall-clock one to startedIso — permanent at empty

    /// <summary>Permanent at empty since E1: OpaqueSchemaTransformer gives both former members a
    /// real schema (GridFilterCollection → string→ColumnFilter map; JsonElement → object with the
    /// CLR type deliberately kept for the DM-1 unknown-field round-trip).</summary>
    private static readonly IReadOnlySet<string> OpaqueSchemaPin = new HashSet<string>(StringComparer.Ordinal);

    [Fact]
    public void No_wire_field_name_means_both_an_instant_and_something_else()
    {
        using var doc = OpenApiDocumentSurface.Load();
        var scope = OpenApiDocumentSurface.ResponseScope(doc);
        var schemas = OpenApiDocumentSurface.Schemas(doc);
        Assert.True(scope.Count >= 100, $"scope {scope.Count} — document walk broken");

        var kindsByName = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var schemaName in scope)
        {
            if (!schemas.TryGetProperty(schemaName, out var schema)
                || !schema.TryGetProperty("properties", out var props)) continue;
            foreach (var prop in props.EnumerateObject())
            {
                var kind = HasDateTimeFormat(prop.Value) ? "date-time" : "other";
                if (!kindsByName.TryGetValue(prop.Name, out var set))
                    kindsByName[prop.Name] = set = new HashSet<string>(StringComparer.Ordinal);
                set.Add(kind);
            }
        }
        Assert.True(kindsByName.Count >= 200,
            $"only {kindsByName.Count} distinct property names — document walk broken");

        var ambiguous = kindsByName.Where(kv => kv.Value.Count > 1).Select(kv => kv.Key)
            .ToHashSet(StringComparer.Ordinal);
        Assert.True(ambiguous.SetEquals(AmbiguousNamePin),
            "instant-vs-other name ambiguity set changed — a name carries format:date-time in one "
            + "schema and not in another. Rename toward the AR-02 terminal suffixes (…Iso for "
            + "wall-clock strings). Now: {" + string.Join(", ", ambiguous) + "}");
    }

    [Fact]
    public void No_component_schema_is_opaque()
    {
        using var doc = OpenApiDocumentSurface.Load();
        var schemas = OpenApiDocumentSurface.Schemas(doc);
        string[] shapeKeys = ["type", "$ref", "properties", "enum", "allOf", "oneOf", "anyOf"];

        var all = 0;
        var opaque = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var schema in schemas.EnumerateObject())
        {
            all++;
            if (!shapeKeys.Any(k => schema.Value.TryGetProperty(k, out _)))
                opaque.Add(schema.Name);
        }
        Assert.True(all >= 150, $"only {all} component schemas — document walk broken");
        Assert.True(opaque.SetEquals(OpaqueSchemaPin),
            "opaque-schema set changed — a schema with no shape renders as TS `unknown` and the "
            + "generated contract stops meaning anything. Give it a type (OpaqueSchemaTransformer, E1) "
            + "or a real schema. Now: {" + string.Join(", ", opaque) + "}");
    }

    private static bool HasDateTimeFormat(JsonElement prop)
    {
        if (prop.TryGetProperty("format", out var f) && f.GetString() == "date-time") return true;
        foreach (var key in (string[])["anyOf", "oneOf", "allOf"])
            if (prop.TryGetProperty(key, out var branches))
                foreach (var b in branches.EnumerateArray())
                    if (HasDateTimeFormat(b)) return true;
        return false;
    }
}
