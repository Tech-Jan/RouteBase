using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace RouteBaseWeb.Api.OpenApiContracts;

/// <summary>
/// The class-level twin of <see cref="CustomConvertedTimestampSchemaTransformer"/>: a type whose
/// (de)serialization is hand-rolled emits an EMPTY schema, which openapi-typescript renders as
/// <c>unknown</c> and tsc stops checking anything that touches it (E1).
///
/// - <c>GridFilterCollection</c> (custom converter): on the wire it IS a string→ColumnFilter map —
///   say so (<c>additionalProperties: $ref ColumnFilter</c>). Inbound win: the client builds that
///   map by hand (activities-grid.ts) and becomes tsc-checked.
/// - <c>JsonElement</c>: an object whose fields are deliberately open. The CLR type MUST stay
///   <c>JsonElement</c> — unknown rule-document fields have to survive parse→resave (DM-1;
///   MinSupportedVersion 2 documents round-trip forever). Typing it as RuleDocument server-side
///   would silently drop unknown fields; this only tells the doc "it's an object".
///
/// A DOCUMENT transformer (not a schema transformer) on purpose: rewriting these mid-generation
/// trips the doc service's schema-copy path ("input schema must be an OpenApiSchema or
/// OpenApiSchemaReference"); patching the assembled components is equivalent and inert.
/// </summary>
internal sealed class OpaqueSchemasDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(OpenApiDocument document, OpenApiDocumentTransformerContext context, CancellationToken cancellationToken)
    {
        var schemas = document.Components?.Schemas;
        if (schemas is null) return Task.CompletedTask;

        if (schemas.TryGetValue("GridFilterCollection", out var gfc)
            && gfc is OpenApiSchema g && g.Type is null)
        {
            g.Type = JsonSchemaType.Object;
            g.AdditionalProperties = new OpenApiSchemaReference("ColumnFilter", document);
        }

        if (schemas.TryGetValue("JsonElement", out var je)
            && je is OpenApiSchema j && j.Type is null)
        {
            j.Type = JsonSchemaType.Object;
        }

        return Task.CompletedTask;
    }
}
