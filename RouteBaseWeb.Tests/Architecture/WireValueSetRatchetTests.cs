using System.Reflection;
using System.Text.Json;
using RouteBaseWeb.Models;
using Xunit;

namespace RouteBaseWeb.Tests.Architecture;

/// <summary>
/// Type-quality gate for CLOSED string vocabularies on the wire (the AR-02 recipe applied to value
/// sets). A wire property whose legal values are a closed set carries
/// <c>[WireValues(typeof(ConstClass))]</c>; the OpenAPI pipeline stamps <c>enum: [...]</c> and
/// openapi-typescript renders a TS literal union — misspelled client comparisons fail tsc.
///
/// Three legs:
/// 1. RATCHET — the pin below lists every candidate closed-set wire property measured 2026-08-01
///    that is NOT yet stamped. It only shrinks: a slice either stamps the property (and deletes the
///    entry) or removes the entry with a written open-set rationale in the same commit. Entries that
///    gained an enum or left the doc are STALE and fail — the pin always reads "exactly what is left
///    to adjudicate".
/// 2. PARITY — for every stamped property, the committed document's enum must equal the const
///    class's <c>public const string</c> fields exactly (both directions), so doc, server validation
///    and the generated TS union cannot drift apart.
/// 3. LOCALIZATION — property-level enums in the response scope, excluding the framework's
///    discriminator literals (<c>op</c>/<c>$type</c>/<c>type</c>, single-value only), must equal the
///    number of stamped properties: a stamp can never leak onto an unattributed property, and a
///    broken scan cannot pass silently.
/// </summary>
public class WireValueSetRatchetTests
{
    /// <summary>Closed-set candidates measured 2026-08-01, "Schema.property" (doc casing). DELETE as
    /// slices stamp them — or remove with a written open-set rationale. Never add silently.</summary>
    private static readonly IReadOnlySet<string> UnstampedPin = new HashSet<string>(StringComparer.Ordinal)
    {
        "AnalysisProgressSnapshot.phase",
        "AssignPhotoResponse.outcome",
        "CommitSummaryResponse.saveMode",
        "DuplicatePhotoConflictBody.outcome",
        "FileResult.outcome",
        "ImportProgressSnapshot.phase",
        "LifecycleTransitionResponse.lifecycle",
        "PhotoFileResult.outcome",
        "PhotoImportSnapshot.phase",
        "ProgressResponseBasicProgressResponse.operationType",
        "ProgressResponseBasicProgressResponse.phase",
        "ProgressResponseIdleProgressResponse.operationType",
        "ProgressResponseIdleProgressResponse.phase",
        "ProgressResponseImportProgressResponse.operationType",
        "ProgressResponseImportProgressResponse.phase",
        "RaceParticipantDto.kind",
        "RaceParticipantRowDto.kind",
        "ShareLinkInventoryItemDto.kind",
        "ShareLinkSummaryDto.kind",
        "ShareRaceParticipantDto.kind",
        "TrackVersionView.provenance",
        "TrashedActivity.reason",
        "TrashedActivity.restoresTo",
    };

    /// <summary>Discriminator properties the framework already stamps ($type polymorphism, EditOp
    /// "op", the K3 rule-document hierarchy's "type"). Excluded from the localization count only as
    /// SINGLE-value literals (a branch's tag) — a genuine multi-value vocabulary under one of these
    /// names would still be counted, so the leak check stays alive for them.</summary>
    private static readonly IReadOnlySet<string> FrameworkEnumProps =
        new HashSet<string>(StringComparer.Ordinal) { "op", "$type", "type" };

    [Fact]
    public void Unstamped_closed_set_wire_properties_match_the_pin_exactly()
    {
        using var doc = OpenApiDocumentSurface.Load();
        var scope = OpenApiDocumentSurface.ResponseScope(doc);
        Assert.True(scope.Count >= 100, $"only {scope.Count} response-side schemas — the document walk is broken");

        var schemas = OpenApiDocumentSurface.Schemas(doc);
        var stillUnstamped = new SortedSet<string>(StringComparer.Ordinal);
        var stale = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var entry in UnstampedPin)
        {
            var dot = entry.IndexOf(".", StringComparison.Ordinal);
            var (schemaName, propName) = (entry[..dot], entry[(dot + 1)..]);
            if (!schemas.TryGetProperty(schemaName, out var schema)
                || !schema.TryGetProperty("properties", out var props)
                || !props.TryGetProperty(propName, out var prop))
            {
                stale.Add(entry + "  (no longer in the document)");
                continue;
            }
            if (prop.TryGetProperty("enum", out _))
                stale.Add(entry + "  (now stamped — delete the pin entry)");
            else
                stillUnstamped.Add(entry);
        }

        Assert.True(stale.Count == 0,
            $"pin entries out of date ({stale.Count}):\n  " + string.Join("\n  ", stale) +
            "\nDelete them in the SAME commit as the change that resolved them.");

        // Growth check: every still-unstamped entry is legal (it IS the pin); nothing to assert on
        // the found side — new closed sets are added to the pin deliberately, with the slice that
        // will stamp them. The pin's purpose is to shrink visibly, not to detect novelty.
        Assert.Equal(UnstampedPin.Count, stillUnstamped.Count);
    }

    [Fact]
    public void Every_stamped_property_matches_its_const_class_exactly()
    {
        using var doc = OpenApiDocumentSurface.Load();
        var scope = OpenApiDocumentSurface.ResponseScope(doc);
        var schemas = OpenApiDocumentSurface.Schemas(doc);
        var app = typeof(WireValuesAttribute).Assembly;
        var byName = OpenApiDocumentSurface.AppTypesByName(app);

        var stampedCount = 0;
        var mismatches = new List<string>();

        foreach (var schemaName in scope)
        {
            foreach (var type in OpenApiDocumentSurface.Resolve(schemaName, byName))
            {
                foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    var attr = p.GetCustomAttribute<WireValuesAttribute>();
                    if (attr is null) continue;

                    var docName = JsonNamingPolicy.CamelCase.ConvertName(p.Name);
                    if (!schemas.TryGetProperty(schemaName, out var schema)
                        || !schema.TryGetProperty("properties", out var props)
                        || !props.TryGetProperty(docName, out var prop)
                        || !prop.TryGetProperty("enum", out var enumEl))
                    {
                        mismatches.Add($"{schemaName}.{docName}: [WireValues] present but NO enum in the doc — " +
                            "rebuild (`dotnet build`) and stage openapi/RouteBaseWeb.json");
                        continue;
                    }

                    stampedCount++;
                    var docValues = enumEl.EnumerateArray().Select(e => e.GetString()!).ToHashSet(StringComparer.Ordinal);
                    var constValues = RouteBaseWeb.Api.OpenApiContracts.WireValueEnumSchemaTransformer
                        .ConstStrings(attr.ConstClass).ToHashSet(StringComparer.Ordinal);
                    if (!docValues.SetEquals(constValues))
                        mismatches.Add($"{schemaName}.{docName}: doc {{{string.Join(",", docValues)}}} != " +
                            $"{attr.ConstClass.Name} {{{string.Join(",", constValues)}}}");
                }
            }
        }

        Assert.True(mismatches.Count == 0,
            "stamped wire vocabularies out of sync with their const classes:\n  " + string.Join("\n  ", mismatches));

        // Localization guard: no stamp leaks onto an unattributed property, and the scan sees the doc.
        var enumProps = 0;
        foreach (var schemaName in scope)
        {
            if (!schemas.TryGetProperty(schemaName, out var schema)
                || !schema.TryGetProperty("properties", out var props)) continue;
            foreach (var prop in props.EnumerateObject())
                if (prop.Value.TryGetProperty("enum", out var vals)
                    && !(FrameworkEnumProps.Contains(prop.Name) && vals.GetArrayLength() == 1))
                    enumProps++;
        }
        Assert.True(enumProps == stampedCount,
            $"{enumProps} property-level enums in the response scope but {stampedCount} [WireValues] stamps — " +
            "an enum leaked onto an unattributed property, or the type mapping is broken");
    }
}
