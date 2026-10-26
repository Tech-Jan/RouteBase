using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace RouteBaseWeb.Api.OpenApiContracts;

/// <summary>
/// A property that carries a custom <c>[JsonConverter]</c> blinds the OpenAPI generator's type
/// inference — it emits an EMPTY schema, which openapi-typescript renders as <c>unknown</c> and the
/// TS mirror loses the field's type. First hit by AR-02:
/// <see cref="Models.Recording.UtcZuluDateTimeOffsetConverter"/> on RecordingSessionDto turned
/// <c>startedAt</c> into <c>unknown</c>. The converter changes the FORMAT of the string, never the
/// type — so restore what the wire actually carries: a date-time string, nullable iff the CLR type is.
/// Guarded on <c>schema.Type is null</c>: only converter-blinded schemas are touched.
/// </summary>
internal sealed class CustomConvertedTimestampSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var t = context.JsonPropertyInfo?.PropertyType;
        if (schema.Type is null && (t == typeof(DateTimeOffset) || t == typeof(DateTimeOffset?)))
        {
            schema.Type = t == typeof(DateTimeOffset?)
                ? JsonSchemaType.String | JsonSchemaType.Null
                : JsonSchemaType.String;
            schema.Format = "date-time";
        }
        return Task.CompletedTask;
    }
}
