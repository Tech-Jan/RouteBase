using System.Text.Json;
using System.Text.Json.Serialization;

namespace RouteBaseWeb.Models.Recording;

/// <summary>
/// Serializes a <see cref="DateTimeOffset"/> as a <c>Z</c>-suffixed UTC instant instead of STJ's default
/// <c>+00:00</c>. Exists for exactly one consumer: GpsRecorder parses <c>startedAt</c> with
/// <c>java.time.Instant.parse</c> (RecordingRepository.startedBefore), whose pre-OpenJDK-12 Android
/// runtimes accept only the <c>Z</c> form. Applied per-property on <see cref="RecordingSessionDto"/> —
/// the one Android-facing shape — so the fleet's wire bytes stay identical across AR-02
/// (RecordingSessionWireTests pins the trailing Z). Browser-facing DTOs stay on the STJ default;
/// JS Date parses both forms.
/// </summary>
public sealed class UtcZuluDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetDateTimeOffset();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        // WriteStringValue(DateTime) with Kind=Utc emits the trailing Z and trims the fraction the same
        // way the raw-DateTime wire did — byte-identical to the pre-migration format.
        => writer.WriteStringValue(value.UtcDateTime);
}
