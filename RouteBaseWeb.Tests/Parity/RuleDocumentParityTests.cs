using RouteBaseWeb.Constants;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RouteBaseWeb.Services.Segments.Rules;
using Xunit;

namespace RouteBaseWeb.Tests.Parity;

/// <summary>
/// Pins the rule-document union in the COMMITTED OpenAPI doc (K3 rewrite of the E1 test — deliberate,
/// strictly stronger). The old test scraped rule-document.ts's hand-written <c>type:</c> literals; the
/// TS side is now GENERATED from the doc and pinned by the drift gate, so the doc itself is what must
/// stay honest. Per polymorphic base, every <c>[JsonDerivedType]</c> discriminator must appear in that
/// base's <c>discriminator.mapping</c> and vice versa — which closes the old test's stated blind spot
/// (a tag in the wrong union) AND catches "RuleDocumentSchemasDocumentTransformer deleted + regen
/// committed", the one drift the doc↔generated gate cannot see (both sides would lose the union
/// together). Vacuity: ≥10 tags total, ≥2 per base.
/// </summary>
public class RuleDocumentParityTests
{
    private static readonly Type[] Bases = [typeof(ShapeGeom), typeof(ExtentRule), typeof(MembershipRule), typeof(TrackFilter)];

    [Fact]
    public void RuleDocumentDiscriminators_CSharpAndOpenApiDoc_MirrorExactlyPerBase()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FindFile("RouteBaseWeb/openapi/RouteBaseWeb.json")));
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");

        int totalTags = 0;
        foreach (var baseType in Bases)
        {
            var csharpTags = baseType.GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
                .Cast<JsonDerivedTypeAttribute>()
                .Select(a => (string)a.TypeDiscriminator!)
                .ToHashSet(StringComparer.Ordinal);
            Assert.True(csharpTags.Count >= 2, $"{baseType.Name}: only {csharpTags.Count} C# discriminators — attribute scrape stale?");
            totalTags += csharpTags.Count;

            Assert.True(schemas.TryGetProperty(baseType.Name, out var schema),
                $"{baseType.Name} is not a component schema — RuleDocumentSchemasDocumentTransformer missing/stale?");
            Assert.True(schema.TryGetProperty("discriminator", out var discriminator),
                $"{baseType.Name} has no discriminator — the union collapsed in the doc.");
            var docTags = discriminator.GetProperty("mapping").EnumerateObject()
                .Select(p => p.Name)
                .ToHashSet(StringComparer.Ordinal);

            Assert.True(csharpTags.SetEquals(docTags),
                $"{baseType.Name} discriminator drift — C# only: [{string.Join(", ", csharpTags.Except(docTags, StringComparer.Ordinal))}], " +
                $"doc only: [{string.Join(", ", docTags.Except(csharpTags, StringComparer.Ordinal))}]. Rebuild to regenerate the doc.");
        }
        Assert.True(totalTags >= 10, $"only {totalTags} discriminators across the bases — base-type list stale?");
    }

    [Fact]
    public void BothRulesProperties_ReferenceRuleDocument()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FindFile("RouteBaseWeb/openapi/RouteBaseWeb.json")));
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        const string reference = "#/components/schemas/RuleDocument";

        // Request: nullable — oneOf [null, $ref RuleDocument] (absent/null rules is a real request state).
        var requestRules = schemas.GetProperty("CreateShapeSegmentRequest").GetProperty("properties").GetProperty("rules");
        var branches = requestRules.GetProperty("oneOf").EnumerateArray().ToList();
        Assert.Equal(2, branches.Count);
        Assert.Contains(branches, b => b.TryGetProperty("type", out var t) && t.GetString() == "null");
        Assert.Contains(branches, b => b.TryGetProperty("$ref", out var r) && r.GetString() == reference);

        // Response: bare ref (never null).
        var responseRules = schemas.GetProperty("ShapeSegmentEditResponse").GetProperty("properties").GetProperty("rules");
        Assert.Equal(reference, responseRules.GetProperty("$ref").GetString());
    }

    [Fact]
    public void RulesVersion_CSharpAndTypeScript_Agree()
    {
        string ts = File.ReadAllText(FindFile("RouteBaseWeb/src/js/overview/shape-filter/rule-document.ts"));
        var m = Regex.Match(ts, @"export const RULES_VERSION = (\d+);", RegexOptions.None, RegexDefaults.MatchTimeout);
        Assert.True(m.Success, "RULES_VERSION not found in rule-document.ts");
        Assert.Equal(RuleDocument.CurrentVersion, int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    private static string FindFile(string rel)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, rel);
            if (File.Exists(candidate)) return candidate;
        }
        throw new FileNotFoundException($"Could not locate {rel} above {AppContext.BaseDirectory}");
    }
}
