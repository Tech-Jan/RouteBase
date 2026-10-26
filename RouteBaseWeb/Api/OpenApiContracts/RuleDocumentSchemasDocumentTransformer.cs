using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using RouteBaseWeb.Constants;
using RouteBaseWeb.Models.Geometry;
using RouteBaseWeb.Services.Segments.Rules;

namespace RouteBaseWeb.Api.OpenApiContracts;

/// <summary>
/// K3: <see cref="RuleDocument"/> enters the OpenAPI doc, and the two <c>rules</c> wire properties
/// (CreateShapeSegmentRequest / ShapeSegmentEditResponse) are repointed from the opaque JsonElement
/// schema to it — the generated TS client gets real discriminated unions (anyOf + discriminator,
/// EditOp precedent) instead of a hand-written mirror in rule-document.ts.
///
/// DM-1 stands: the CLR carriers STAY <see cref="System.Text.Json.JsonElement"/> — parsing must keep
/// flowing through <see cref="RuleDocument.TryParse"/> (one parser, one set of serializer options),
/// and unknown fields in stored v2 documents must survive parse→resave. This transformer changes the
/// DOCUMENT only.
///
/// Why the lifting pass: the framework componentizes polymorphic hierarchies (base = anyOf of
/// {Base}{Derived} refs + discriminator.mapping) only on the ENDPOINT pipeline. A schema created via
/// GetOrCreateSchemaAsync inside a document transformer arrives as one fully-INLINE tree — with the
/// discriminator mappings already pointing at "#/components/schemas/{Base}{Derived}" names that
/// nothing registered. The lift below materializes exactly the components those emitted mappings
/// name (branch identity = its discriminator literal; name = C# base+derived type names, the same
/// scheme the emitter uses — asserted after each lift so a framework naming change fails the build).
///
/// Presence honesty: RuleDocument is a verbatim-stored, CLIENT-authored document, so <c>required</c>
/// may keep only what save-time validation (<see cref="RuleDocument.Validate"/>) actually guarantees.
/// The D1 "server always writes it" rule stamped by RequiredPropertiesSchemaTransformer does not hold
/// here — stored v2 docs are echoed back verbatim, missing every later-era property. Concretely:
/// RuleDocument keeps [version, shapes, extent, membership]; PassesAnyRule drops <c>group</c>,
/// EndpointInZoneRule drops <c>endpoint</c>, DateRangeFilter drops <c>from</c>/<c>to</c> (each has a
/// server-side default / is genuinely optional). ColumnFilter loses its required list entirely: the
/// repoint makes it response-reachable (dual-use), which stops gen-contracts' D26 request-only
/// relaxation — without this every hand-built filter map would fail tsc against all-required keys the
/// server never demands.
///
/// Registered LAST so it patches the assembled components after the other document transformers.
/// </summary>
internal sealed class RuleDocumentSchemasDocumentTransformer : IOpenApiDocumentTransformer
{
    public async Task TransformAsync(
        OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        // 1) Generate the inline tree (schema transformers apply: D1/D3/D26 stamps, D30 discriminator
        //    forcing) and lift it into EditOp-style components.
        var root = await context.GetOrCreateSchemaAsync(typeof(RuleDocument), parameterDescription: null, cancellationToken)
            as OpenApiSchema ?? throw new InvalidOperationException("RuleDocument did not export as an inline OpenApiSchema.");
        document.Components ??= new OpenApiComponents();
        var schemas = document.Components.Schemas ??= new Dictionary<string, IOpenApiSchema>(StringComparer.Ordinal);

        var shapes = Prop(root, "shapes");
        var shapeDef = shapes.Items as OpenApiSchema
            ?? throw new InvalidOperationException("RuleDocument.shapes.items is not an inline schema.");
        LiftEnum(document, schemas, Prop(LineGeomBranch(shapeDef), "direction"), nameof(CrossDirection),
            (LineGeomBranch(shapeDef), "direction"));
        LiftUnion(document, schemas, typeof(ShapeGeom), shapeDef, "geom");
        shapes.Items = AddComponent(document, schemas, nameof(ShapeDef), shapeDef);
        SetProp(root, "shapes", shapes);

        LiftUnion(document, schemas, typeof(ExtentRule), root, "extent");

        var membership = Prop(root, "membership");
        var membershipBase = membership.Items as OpenApiSchema
            ?? throw new InvalidOperationException("RuleDocument.membership.items is not an inline schema.");
        LiftEnum(document, schemas, Prop(BranchByTag(membershipBase, "endpointInZone"), "endpoint"),
            nameof(Services.Segments.Rules.Endpoint),
            (BranchByTag(membershipBase, "endpointInZone"), "endpoint"));
        membership.Items = LiftUnionNode(document, schemas, typeof(MembershipRule), membershipBase);

        var trackFilters = Prop(root, "trackFilters");
        var trackFilterBase = trackFilters.Items as OpenApiSchema
            ?? throw new InvalidOperationException("RuleDocument.trackFilters.items is not an inline schema.");
        // GridFiltersFilter's map values are the EXISTING ColumnFilter component — repoint, don't duplicate.
        var gridFilters = Prop(BranchByTag(trackFilterBase, "gridFilters"), "filters");
        if (gridFilters.AdditionalProperties is not OpenApiSchema { Properties.Count: > 0 })
            throw new InvalidOperationException("gridFilters.filters no longer carries an inline ColumnFilter map.");
        gridFilters.AdditionalProperties = new OpenApiSchemaReference("ColumnFilter", document);
        trackFilters.Items = LiftUnionNode(document, schemas, typeof(TrackFilter), trackFilterBase);

        var ruleDocumentRef = AddComponent(document, schemas, nameof(RuleDocument), root);

        // 2) Repoint both rules properties, verifying the pre-shape so a DTO drift fails loudly.
        //    CreateShapeSegmentRequest.rules keeps its null branch (JsonElement? — "absent/null rules"
        //    is a real request state); ShapeSegmentEditResponse.rules is a bare ref (never null).
        var request = RequireSchema(document, "CreateShapeSegmentRequest");
        var requestRules = PropAny(request, "rules");
        if (requestRules is not { OneOf: { Count: 2 } oneOf }
            || !oneOf.Any(b => b is OpenApiSchema { Type: JsonSchemaType.Null })
            || !oneOf.Any(b => IsRefTo(b, "JsonElement")))
        {
            throw new InvalidOperationException(
                "CreateShapeSegmentRequest.rules is no longer oneOf [null, JsonElement] — re-derive this repoint.");
        }
        SetProp(request, "rules", new OpenApiSchema
        {
            OneOf = [new OpenApiSchema { Type = JsonSchemaType.Null }, ruleDocumentRef],
        });

        var response = RequireSchema(document, "ShapeSegmentEditResponse");
        if (!IsRefTo(PropAny(response, "rules"), "JsonElement"))
            throw new InvalidOperationException(
                "ShapeSegmentEditResponse.rules is no longer a bare JsonElement ref — re-derive this repoint.");
        SetProp(response, "rules", ruleDocumentRef);

        // 3) Presence-honesty pass (rule + per-site rationale in the class doc comment).
        KeepRequired(document, nameof(RuleDocument), "version", "shapes", "extent", "membership");
        DropRequired(document, "MembershipRulePassesAnyRule", "group");
        DropRequired(document, "MembershipRuleEndpointInZoneRule", "endpoint");
        DropRequired(document, "TrackFilterDateRangeFilter", "from");
        DropRequired(document, "TrackFilterDateRangeFilter", "to");
        // TrackFilter.Discriminator is a C# get-only convenience (duplicate-type error messages) that
        // STJ writes on save — present in server-stored docs, but never client-authored and never
        // validated, so it stays in the schema as optional only.
        DropRequired(document, "TrackFilterDateRangeFilter", "discriminator");
        DropRequired(document, "TrackFilterActivityTypeFilter", "discriminator");
        DropRequired(document, "TrackFilterOwnerFilter", "discriminator");
        DropRequired(document, "TrackFilterGridFiltersFilter", "discriminator");
        RequireSchema(document, "ColumnFilter").Required = null;
    }

    /// <summary>Lift the polymorphic base node at <paramref name="container"/>.<paramref name="property"/>
    /// into components and replace the property site with a ref to the base.</summary>
    private static void LiftUnion(OpenApiDocument document, IDictionary<string, IOpenApiSchema> schemas,
        Type baseType, OpenApiSchema container, string property)
    {
        var baseNode = Prop(container, property);
        SetProp(container, property, LiftUnionNode(document, schemas, baseType, baseNode));
    }

    /// <summary>Lift each anyOf branch (identified by its discriminator literal, named
    /// {Base}{Derived} from the C# types — the emitter's own scheme) and then the base node itself;
    /// returns the base ref. Asserts afterwards that every emitted mapping target got registered.</summary>
    private static OpenApiSchemaReference LiftUnionNode(OpenApiDocument document,
        IDictionary<string, IOpenApiSchema> schemas, Type baseType, OpenApiSchema baseNode)
    {
        string discriminator = baseNode.Discriminator?.PropertyName
            ?? throw new InvalidOperationException($"{baseType.Name}: exported schema has no discriminator.");
        var tagToName = baseType.GetCustomAttributes(typeof(JsonDerivedTypeAttribute), inherit: false)
            .Cast<JsonDerivedTypeAttribute>()
            .ToDictionary(
                a => a.TypeDiscriminator as string
                    ?? throw new InvalidOperationException($"{baseType.Name}: non-string [JsonDerivedType] tag."),
                a => baseType.Name + a.DerivedType.Name,
                StringComparer.Ordinal);
        var branches = baseNode.AnyOf
            ?? throw new InvalidOperationException($"{baseType.Name}: exported schema has no anyOf branches.");
        if (branches.Count != tagToName.Count)
            throw new InvalidOperationException(
                $"{baseType.Name}: {branches.Count} anyOf branches vs {tagToName.Count} [JsonDerivedType] tags.");

        baseNode.AnyOf = branches
            .Select(b => (IOpenApiSchema)AddComponent(document, schemas, tagToName[TagOf(b, discriminator, baseType)], b))
            .ToList();
        var baseRef = AddComponent(document, schemas, baseType.Name, baseNode);

        // Naming-scheme pin: the emitted mapping must reference exactly the components just added.
        foreach (Match m in Regex.Matches(
            Serialized(baseNode), @"#/components/schemas/(\w+)", RegexOptions.None, RegexDefaults.MatchTimeout))
            if (!schemas.ContainsKey(m.Groups[1].Value))
                throw new InvalidOperationException(
                    $"{baseType.Name}: discriminator mapping targets unregistered '{m.Groups[1].Value}' — " +
                    "the framework's derived-schema naming scheme changed; update the lift.");
        return baseRef;
    }

    /// <summary>The branch's discriminator literal, e.g. "rect" from <c>type: {enum: [rect]}</c>.</summary>
    private static string TagOf(IOpenApiSchema branch, string discriminator, Type baseType)
    {
        if (branch is OpenApiSchema b && b.Properties is { } props && props.TryGetValue(discriminator, out var t)
            && t is OpenApiSchema { Enum: { Count: 1 } values } && values[0]?.GetValue<string>() is { } literal)
            return literal;
        throw new InvalidOperationException($"{baseType.Name}: an anyOf branch has no single '{discriminator}' literal.");
    }

    /// <summary>Register an inline enum node (e.g. CrossDirection) as a component and repoint its
    /// single use site — the same shape the endpoint pipeline gives TimeMode.</summary>
    private static void LiftEnum(OpenApiDocument document, IDictionary<string, IOpenApiSchema> schemas,
        OpenApiSchema node, string name, (OpenApiSchema Container, string Property) site)
    {
        if (node.Enum is not { Count: > 1 })
            throw new InvalidOperationException($"'{name}' site is not an inline enum — lift assumptions are stale.");
        SetProp(site.Container, site.Property, AddComponent(document, schemas, name, node));
    }

    private static OpenApiSchemaReference AddComponent(OpenApiDocument document,
        IDictionary<string, IOpenApiSchema> schemas, string name, IOpenApiSchema node)
    {
        if (!schemas.TryAdd(name, node))
            throw new InvalidOperationException(
                $"Component schema '{name}' already exists — another type shares the short name (rename one), " +
                "or it became endpoint-reachable on its own (then this transformer's lift of it is obsolete).");
        return new OpenApiSchemaReference(name, document);
    }

    /// <summary>The LineGeom branch of ShapeDef.geom, located by its "line" discriminator literal.</summary>
    private static OpenApiSchema LineGeomBranch(OpenApiSchema shapeDef) => BranchByTag(Prop(shapeDef, "geom"), "line");

    private static OpenApiSchema BranchByTag(OpenApiSchema baseNode, string tag)
    {
        string discriminator = baseNode.Discriminator?.PropertyName ?? "type";
        var branch = baseNode.AnyOf?.OfType<OpenApiSchema>().FirstOrDefault(b =>
            b.Properties is { } props && props.TryGetValue(discriminator, out var t)
            && t is OpenApiSchema { Enum: { Count: 1 } v } && v[0]?.GetValue<string>() == tag);
        return branch ?? throw new InvalidOperationException($"No anyOf branch with {discriminator}={tag}.");
    }

    private static string Serialized(IOpenApiSchema schema)
    {
        using var text = new StringWriter();
        schema.SerializeAsV31(new OpenApiJsonWriter(text));
        return text.ToString();
    }

    /// <summary>The ASP.NET generator's schema references resolve their Id LATE (empty at
    /// document-transformer time), so a ref target is only observable via serialization.</summary>
    private static bool IsRefTo(IOpenApiSchema schema, string name) =>
        schema is OpenApiSchemaReference
        && Serialized(schema).Contains($"#/components/schemas/{name}\"", StringComparison.Ordinal);

    private static OpenApiSchema RequireSchema(OpenApiDocument document, string name)
    {
        if (document.Components?.Schemas is not { } schemas || !schemas.TryGetValue(name, out var schema))
            throw new InvalidOperationException($"Component schema '{name}' is missing — this transformer's assumptions are stale.");
        return schema as OpenApiSchema
            ?? throw new InvalidOperationException($"Component schema '{name}' is not an inline OpenApiSchema.");
    }

    private static OpenApiSchema Prop(OpenApiSchema schema, string name) =>
        PropAny(schema, name) as OpenApiSchema
            ?? throw new InvalidOperationException($"Property '{name}' is not an inline OpenApiSchema.");

    private static IOpenApiSchema PropAny(OpenApiSchema schema, string name) =>
        schema.Properties is { } props && props.TryGetValue(name, out var value)
            ? value
            : throw new InvalidOperationException($"Expected property '{name}' is missing — this transformer's assumptions are stale.");

    /// <summary>Overwrite an EXISTING property slot (reads went through <see cref="Prop"/> first, so
    /// absence here means the pre-shape checks were bypassed).</summary>
    private static void SetProp(OpenApiSchema schema, string name, IOpenApiSchema value)
    {
        if (schema.Properties is not { } props || !props.ContainsKey(name))
            throw new InvalidOperationException($"Cannot repoint missing property '{name}'.");
        props[name] = value;
    }

    /// <summary>Shrink <c>required</c> to exactly <paramref name="keep"/> (all of which must be present).</summary>
    private static void KeepRequired(OpenApiDocument document, string name, params string[] keep)
    {
        var schema = RequireSchema(document, name);
        var missing = keep.Where(k => schema.Required?.Contains(k) != true).ToList();
        if (missing.Count > 0)
            throw new InvalidOperationException($"'{name}' no longer requires [{string.Join(", ", missing)}] — honesty list is stale.");
        schema.Required = new HashSet<string>(keep, StringComparer.Ordinal);
    }

    private static void DropRequired(OpenApiDocument document, string name, string property)
    {
        var schema = RequireSchema(document, name);
        if (schema.Required?.Remove(property) != true)
            throw new InvalidOperationException($"'{name}.{property}' was not required — honesty list is stale.");
    }
}
