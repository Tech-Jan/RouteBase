using System.Text.Json;
using System.Text.Json.Serialization;

namespace RouteBaseWeb.Services.TrackEdit;

/// <summary>
/// The frozen op/script wire contract (A1 §2.1/§2.4). One serialization serves the wire, session
/// storage and the parity fixtures. Discriminator values ("op") are the grep keys — never build them
/// from fragments. Indices are point ordinals in the referenced slot's CURRENT state (previous op's
/// output); ranges are inclusive both ends. Contract changes here are atomic with the TS mirror
/// (edit-ops.ts) per invariant 8 — freeze once I1 lands.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "op")]
[JsonDerivedType(typeof(LoadActivityOp), "loadActivity")]
[JsonDerivedType(typeof(CropToOp), "cropTo")]
[JsonDerivedType(typeof(DeleteRangeOp), "deleteRange")]
[JsonDerivedType(typeof(SplitAtOp), "splitAt")]
[JsonDerivedType(typeof(AppendOp), "append")]
public abstract record EditOp;

public sealed record LoadActivityOp(int ActivityId) : EditOp;

public sealed record CropToOp(int Slot, int From, int To) : EditOp;

public sealed record DeleteRangeOp(int Slot, int From, int To, TimeMode TimeMode, long? GapSeconds) : EditOp;

public sealed record SplitAtOp(int Slot, int Index) : EditOp;

public sealed record AppendOp(int Target, int Source, TimeMode TimeMode, long? GapSeconds) : EditOp;

/// <summary>What happens to the next run's timestamps at a junction — the single definition (invariant 9,
/// A1 §2.2). <c>Keep</c> leaves them untouched; <c>Close</c>/<c>Synthesize</c> shift them so the junction
/// gap becomes exactly <c>GapSeconds</c> (the two differ only in how the UI computed that gap).
/// The camelCase-string wire form is declared ON THE TYPE, not just in TrackEditJson.Options: ops also
/// cross MVC's [FromBody] binder / response serializer, which use their own options.</summary>
[JsonConverter(typeof(TimeModeJsonConverter))]
public enum TimeMode { Keep, Close, Synthesize }

/// <summary>Pins TimeMode's wire form to "keep"/"close"/"synthesize" under ANY JsonSerializerOptions.</summary>
public sealed class TimeModeJsonConverter : JsonStringEnumConverter<TimeMode>
{
    public TimeModeJsonConverter() : base(JsonNamingPolicy.CamelCase) { }
}

/// <summary>An ordered edit script. <c>SchemaVersion</c> is checked once at parse (invariant 10 —
/// version from day 1); per-op version fields would be redundant.</summary>
public sealed record EditScript(int SchemaVersion, IReadOnlyList<EditOp> Ops);

/// <summary>The single JsonSerializerOptions for script/session/fixture (de)serialization — never inline
/// options. camelCase properties (enum casing rides TimeMode's own converter); gapSeconds omitted when null.</summary>
public static class TrackEditJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
