using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;
using RouteBaseWeb.Models;

namespace RouteBaseWeb.Api.OpenApiContracts;

/// <summary>
/// Stamps <c>enum: [...]</c> onto every string property carrying <see cref="WireValuesAttribute"/>,
/// reflecting the named const class's <c>public const string</c> fields — the same attribute-reading
/// mechanism as <see cref="RequiredPropertiesSchemaTransformer"/>. openapi-typescript renders the
/// result as a TS string literal union, so the closed set becomes compiler-checked client-side.
///
/// Wire-neutral by construction: the attribute is runtime-inert and this only ADDS schema metadata;
/// serialized JSON values are untouched. <c>WireValueSetRatchetTests</c> asserts doc↔const parity and
/// that stamps never leak onto unattributed properties.
/// </summary>
internal sealed class WireValueEnumSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        var attr = (context.JsonPropertyInfo?.AttributeProvider as MemberInfo)
            ?.GetCustomAttribute<WireValuesAttribute>();
        if (attr is null) return Task.CompletedTask;

        var values = ConstStrings(attr.ConstClass);
        if (values.Count == 0)
            throw new InvalidOperationException(
                $"[WireValues(typeof({attr.ConstClass.Name}))] names a class with no public const string fields");

        schema.Enum = [.. values.Select(v => (JsonNode)JsonValue.Create(v))];
        return Task.CompletedTask;
    }

    /// <summary>The legal wire values: every public const string on the class (declaration order).
    /// Distinct because alias consts are legal (Visibilities.Default = Friends) and a duplicated
    /// enum entry would be a doc bug.</summary>
    internal static IReadOnlyList<string> ConstStrings(Type constClass) =>
        [.. constClass
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly)
            .Where(f => f is { IsLiteral: true, FieldType.Name: nameof(String) })
            .Select(f => f.GetRawConstantValue() as string
                ?? throw new InvalidOperationException($"non-string const on wire-value type: {f.Name}"))
            .Distinct(StringComparer.Ordinal)];
}
