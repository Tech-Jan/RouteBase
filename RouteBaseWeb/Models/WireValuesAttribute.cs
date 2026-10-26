namespace RouteBaseWeb.Models;

/// <summary>
/// Declares the CLOSED set of wire values for a string DTO property by naming the const class that
/// owns them (e.g. <c>[property: WireValues(typeof(RecordingSessionStatus))]</c>). Runtime-inert:
/// serialization is untouched — the OpenAPI pipeline (<c>WireValueEnumSchemaTransformer</c>) reads it
/// and stamps <c>enum: [...]</c> into the document, which openapi-typescript renders as a TS string
/// literal union, so a client-side comparison against a misspelled value fails <c>tsc</c>.
///
/// The C# property stays <c>string</c> on purpose (no EF converter risk, no serialization change);
/// the const class carries <c>All</c>/<c>IsValid</c> for the server side.
/// <c>WireValueSetRatchetTests</c> keeps doc and const class in lockstep and pins which closed-set
/// wire properties are still unstamped.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class WireValuesAttribute(Type constClass) : Attribute
{
    /// <summary>The class whose <c>public const string</c> fields are the legal wire values.</summary>
    public Type ConstClass { get; } = constClass;
}
