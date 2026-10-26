using System.Reflection;
using System.Text.Json;

namespace RouteBaseWeb.Tests.Architecture;

/// <summary>
/// The committed OpenAPI document as a queryable wire surface — extracted from
/// <see cref="ResponseDtoDateTimeRatchetTests"/> (rule of three: the type-quality gates wanted the
/// same walk). AR-02's gate staying green on this shared code is the proof the extraction is sound.
///
/// "Response scope" = every component schema a client can RECEIVE: response-reachable schemas plus
/// the registered JSON islands (in the doc with no endpoint). Request-ONLY schemas are excluded —
/// parsed, not emitted. Same closure walk as <c>scripts/gen-contracts.mjs</c> (D26).
/// </summary>
internal static class OpenApiDocumentSurface
{
    internal const string DocPath = "RouteBaseWeb/openapi/RouteBaseWeb.json";
    private const string SchemaRefPrefix = "#/components/schemas/";

    internal static JsonDocument Load() =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(ProductTree.RepoRoot(), DocPath)));

    /// <summary>All component schema names minus the request-only closure.</summary>
    internal static HashSet<string> ResponseScope(JsonDocument doc)
    {
        var schemas = Schemas(doc);
        var allNames = schemas.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        var reqRoots = new HashSet<string>(StringComparer.Ordinal);
        var resRoots = new HashSet<string>(StringComparer.Ordinal);
        foreach (var pathItem in doc.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var op in pathItem.Value.EnumerateObject())
            {
                if (op.Value.ValueKind != JsonValueKind.Object) continue;
                if (op.Value.TryGetProperty("requestBody", out var rb)) CollectRefs(rb, reqRoots);
                if (op.Value.TryGetProperty("responses", out var rs)) CollectRefs(rs, resRoots);
                // Parameter schemas: treated as response-side (non-relaxable) by gen-contracts; mirror it.
                if (op.Value.TryGetProperty("parameters", out var ps)) CollectRefs(ps, resRoots);
            }
        }
        var requestOnly = Closure(schemas, reqRoots);
        requestOnly.ExceptWith(Closure(schemas, resRoots));

        return allNames.Except(requestOnly, StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
    }

    internal static JsonElement Schemas(JsonDocument doc) =>
        doc.RootElement.GetProperty("components").GetProperty("schemas");

    /// <summary>Product-assembly types indexed by short name (open generics and compiler junk excluded).</summary>
    internal static Dictionary<string, List<Type>> AppTypesByName(Assembly app) =>
        app.GetTypes()
            .Where(t => !t.IsGenericTypeDefinition && t.Name[0] != '<')
            .GroupBy(t => t.Name, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

    /// <summary>All types a schema name may refer to: exact short-name match first, else the
    /// $type-polymorphism mangling "{Base}{Derived}" (D30), accepted only when BOTH halves resolve.
    /// A short-name collision returns every candidate — walking a same-named non-wire twin errs
    /// toward FINDING a violation, and a wrong hit fails loudly at pin time rather than silently.</summary>
    internal static List<Type> Resolve(string schemaName, Dictionary<string, List<Type>> byName)
    {
        if (byName.TryGetValue(schemaName, out var exact)) return exact;

        foreach (var (suffixName, candidates) in byName)
        {
            if (!schemaName.EndsWith(suffixName, StringComparison.Ordinal) ||
                schemaName.Length == suffixName.Length) continue;
            var prefix = schemaName[..^suffixName.Length];
            if (byName.ContainsKey(prefix)) return candidates; // "{Base}{Derived}" mangling
        }
        return [];
    }

    internal static void CollectRefs(JsonElement node, HashSet<string> into)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in node.EnumerateObject())
                {
                    if (p.NameEquals("$ref") && p.Value.ValueKind == JsonValueKind.String &&
                        p.Value.GetString() is { } r && r.StartsWith(SchemaRefPrefix, StringComparison.Ordinal))
                        into.Add(r[SchemaRefPrefix.Length..]);
                    else
                        CollectRefs(p.Value, into);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in node.EnumerateArray()) CollectRefs(item, into);
                break;
        }
    }

    internal static HashSet<string> Closure(JsonElement schemas, HashSet<string> roots)
    {
        var seen = new HashSet<string>(roots, StringComparer.Ordinal);
        var queue = new Queue<string>(roots);
        while (queue.Count > 0)
        {
            var name = queue.Dequeue();
            if (!schemas.TryGetProperty(name, out var schema)) continue;
            var refs = new HashSet<string>(StringComparer.Ordinal);
            CollectRefs(schema, refs);
            foreach (var r in refs)
                if (seen.Add(r)) queue.Enqueue(r);
        }
        return seen;
    }
}
