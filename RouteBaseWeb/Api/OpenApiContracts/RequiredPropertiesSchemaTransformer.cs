using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace RouteBaseWeb.Api.OpenApiContracts;

/// <summary>
/// Makes the generated OpenAPI schema match System.Text.Json's ACTUAL wire behaviour for property presence —
/// the thing the TS client contracts (src/js/generated) are generated from.
///
/// The problem: .NET's OpenAPI generator marks a property <c>required</c> only when it is a C# <c>required</c>
/// member or <c>[JsonRequired]</c>. Our DTOs are positional records, so NOTHING is required — every field would
/// become optional (<c>field?: T</c>) in TypeScript, erasing the null-vs-omitted distinction the DTO↔TS
/// nullability pass established by hand.
///
/// The real rule (there is no <c>DefaultIgnoreCondition</c> configured server-side): every serialized property
/// is ALWAYS written, so it is <c>required</c> on the wire — EXCEPT one gated by
/// <c>[JsonIgnore(Condition = WhenWritingNull | WhenWritingDefault)]</c>, which may be absent. Nullability of the
/// value is orthogonal and already emitted correctly by the generator (<c>type: ["null","string"]</c>). Together
/// with openapi-typescript this reproduces the intended shapes:
///   non-null, always-written  → <c>field: T</c>
///   nullable, always-written   → <c>field: T | null</c>
///   [JsonIgnore(WhenWritingNull)] → <c>field?: T | null</c>
///
/// NOTE (reviewed and accepted): this marks request-DTO fields required
/// too, so a client must send a nullable field explicitly rather than omit it. Acceptable — our request bodies
/// are built as fully-populated typed objects — and the alternative (per-usage request/response split) is not
/// expressible in a schema transformer.
/// </summary>
internal sealed class RequiredPropertiesSchemaTransformer : IOpenApiSchemaTransformer
{
    public Task TransformAsync(OpenApiSchema schema, OpenApiSchemaTransformerContext context, CancellationToken cancellationToken)
    {
        // Strip the phantom "string" member that JsonSerializerDefaults.Web (NumberHandling =
        // AllowReadingFromString, which MVC uses) adds to every numeric schema. That is a DESERIALIZE-side
        // leniency; responses always WRITE numbers, so the TS client only ever reads `number`. (Logged D3.)
        if (IsNumeric(context.JsonTypeInfo.Type) && schema.Type is { } t && (t & (JsonSchemaType.Integer | JsonSchemaType.Number)) != 0)
            schema.Type = t & ~JsonSchemaType.String;

        if (context.JsonTypeInfo.Kind != JsonTypeInfoKind.Object || schema.Properties is null)
            return Task.CompletedTask;

        // D26: preserve STJ's DESERIALIZE-required set (C# `required`/[JsonRequired] — what .NET emitted
        // before this transformer overwrites it below). gen-contracts.mjs relaxes request-only schemas back
        // to this set, because a request may omit anything else (STJ binds the default). Stamped only when
        // non-empty — positional records without [JsonRequired] have nothing deserialize-required.
        if (schema.Required is { Count: > 0 } deserializeRequired)
        {
            schema.Extensions ??= new Dictionary<string, IOpenApiExtension>(StringComparer.Ordinal);
            // Ordinal, not Order()'s default (Comparer<string>.Default = CurrentCulture): this ordering is
            // baked into the committed OpenAPI doc that generates src/js/generated, so a machine-readable
            // contract must not shift with the build machine's locale.
            schema.Extensions["x-deserialize-required"] =
                new JsonNodeExtension(JsonSerializer.SerializeToNode(deserializeRequired.Order(StringComparer.Ordinal).ToArray())
                ?? throw new InvalidOperationException("serializing a string[] never yields null"));
        }

        schema.Required ??= new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonPropertyInfo property in context.JsonTypeInfo.Properties)
        {
            // Only touch names the schema actually emitted (respects [JsonIgnore(Always)], which drops the prop).
            if (!schema.Properties.ContainsKey(property.Name))
                continue;

            if (IsConditionallyOmitted(property))
                schema.Required.Remove(property.Name);
            else
                schema.Required.Add(property.Name);
        }

        // D30 (generalized): a [JsonPolymorphic] derived schema carries a synthetic discriminator property
        // (the hierarchy's TypeDiscriminatorPropertyName) that is NOT a real JsonPropertyInfo, so the loop
        // above never sees it, and .NET does NOT mark it required on the derived branch. Left optional,
        // openapi-typescript emits `$type?: "…"` (or `op?:`, `type?:` — not every hierarchy uses `$type`)
        // and the union stops narrowing. Force it required wherever the schema emitted it — the wire always
        // writes it first, so this is truthful. (The polymorphic BASE already gets it required from .NET;
        // this closes the same gap on each derived branch.)
        if (DiscriminatorNameFor(context.JsonTypeInfo.Type) is { } discriminator
            && schema.Properties.ContainsKey(discriminator))
            schema.Required.Add(discriminator);

        return Task.CompletedTask;
    }

    /// <summary>The discriminator name of the hierarchy this type belongs to — the nearest
    /// [JsonPolymorphic] up the base chain (its default, when unset, is STJ's "$type"). Null for
    /// non-polymorphic types.</summary>
    static string? DiscriminatorNameFor(Type type)
    {
        for (Type? t = type; t is not null; t = t.BaseType)
            if (t.GetCustomAttribute<JsonPolymorphicAttribute>(inherit: false) is { } poly)
                return poly.TypeDiscriminatorPropertyName ?? "$type";
        return null;
    }

    // A property STJ may omit from the wire: [JsonIgnore(Condition = WhenWritingNull | WhenWritingDefault)].
    static bool IsConditionallyOmitted(JsonPropertyInfo property)
    {
        var ignore = (property.AttributeProvider as MemberInfo)?.GetCustomAttribute<JsonIgnoreAttribute>();
        return ignore is { Condition: JsonIgnoreCondition.WhenWritingNull or JsonIgnoreCondition.WhenWritingDefault };
    }

    static bool IsNumeric(Type type)
    {
        Type t = Nullable.GetUnderlyingType(type) ?? type;
        return t == typeof(int) || t == typeof(long) || t == typeof(short) || t == typeof(byte)
            || t == typeof(uint) || t == typeof(ulong) || t == typeof(ushort) || t == typeof(sbyte)
            || t == typeof(double) || t == typeof(float) || t == typeof(decimal);
    }
}
