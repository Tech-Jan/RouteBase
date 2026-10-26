using System.Globalization;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using Directory = MetadataExtractor.Directory;
using System.Diagnostics.CodeAnalysis;

namespace RouteBaseWeb.Services.Photos;

/// <summary>Parsed EXIF facts the import pipeline needs (Phase-1 §3/§11). Capture time is the raw wall-clock
/// <c>DateTimeOriginal</c> (no zone); tz resolution to UTC happens in the orchestrator.</summary>
public sealed record PhotoExif(
    DateTime? DateTimeOriginalLocal,
    TimeSpan? UtcOffset,
    double? GpsLat,
    double? GpsLng,
    string? CameraModel)
{
    [MemberNotNullWhen(true, nameof(DateTimeOriginalLocal))]
    public bool HasTime => DateTimeOriginalLocal.HasValue;
    public bool HasGps => GpsLat.HasValue && GpsLng.HasValue;

    public static readonly PhotoExif Empty = new(null, null, null, null, null);
}

/// <summary>
/// Reads EXIF via MetadataExtractor. Pure over the input stream — no DB/FS side effects. Never throws on
/// missing/partial metadata (returns nulls): stripped sources (Signal) legitimately have none (§11).
/// </summary>
public sealed class PhotoExifReader
{
    private const int TagOffsetTimeOriginal = 0x9011;
    private const int TagSubsecTimeOriginal = 0x9291;

    public PhotoExif Read(Stream stream)
    {
        IReadOnlyList<Directory> dirs;
        try
        {
            dirs = ImageMetadataReader.ReadMetadata(stream);
        }
        catch
        {
            // Unreadable/absent metadata is expected for stripped files — treat as no EXIF, not a failure.
            return PhotoExif.Empty;
        }

        var sub  = dirs.OfType<ExifSubIfdDirectory>().FirstOrDefault();
        var ifd0 = dirs.OfType<ExifIfd0Directory>().FirstOrDefault();
        var gps  = dirs.OfType<GpsDirectory>().FirstOrDefault();

        DateTime? dto = null;
        if (sub != null && sub.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var d))
        {
            // Fold in sub-second precision so burst frames that share the whole-second DateTimeOriginal get
            // distinct capture instants — the D5 same-time dedup keys on captured_utc, so without this a
            // burst collapses to one photo. Guard on Millisecond==0 in case a future MetadataExtractor folds
            // subsec in itself (don't double-count).
            if (d.Millisecond == 0 && ParseSubsecondMs(sub.GetString(TagSubsecTimeOriginal)) is { } ms)
                d = d.AddMilliseconds(ms);
            dto = DateTime.SpecifyKind(d, DateTimeKind.Unspecified);
        }

        TimeSpan? offset = ParseOffset(sub?.GetString(TagOffsetTimeOriginal));

        double? lat = null, lng = null;
        var loc = gps?.GetGeoLocation();
        if (loc is { } l && !l.IsZero) { lat = l.Latitude; lng = l.Longitude; }

        string? model = BuildModel(
            ifd0?.GetString(ExifDirectoryBase.TagMake),
            ifd0?.GetString(ExifDirectoryBase.TagModel));

        return new PhotoExif(dto, offset, lat, lng, model);
    }

    // EXIF SubSecTimeOriginal = fractional-second digits ("174" → .174s, "5" → .5s). Milliseconds in
    // [0,1000), or null when absent/non-numeric.
    private static double? ParseSubsecondMs(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return double.TryParse("0." + raw.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var frac)
            ? frac * 1000.0
            : null;
    }

    // "+02:00" / "-05:30" / "Z" → TimeSpan. Null on absent/malformed.
    // internal for the sign-handling pins in PhotoExifReaderTests (InternalsVisibleTo RouteBaseWeb.Tests).
    internal static TimeSpan? ParseOffset(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        raw = raw.Trim();
        if (raw is "Z" or "z") return TimeSpan.Zero;
        // Trim BOTH signs: the `hh\:mm` format has no sign token, so a leading '-' left in place fails the
        // parse and every negative offset ("-05:00") returned null before the sign negation below could run.
        return TimeSpan.TryParseExact(raw.TrimStart('+', '-'), @"hh\:mm", CultureInfo.InvariantCulture, out var ts)
            ? (raw.StartsWith('-') ? -ts : ts)
            : null;
    }

    private static string? BuildModel(string? make, string? model)
    {
        make = make?.Trim();
        model = model?.Trim();
        if (string.IsNullOrEmpty(model)) return string.IsNullOrEmpty(make) ? null : make;
        if (string.IsNullOrEmpty(make) || model.StartsWith(make, StringComparison.OrdinalIgnoreCase)) return model;
        return $"{make} {model}";
    }
}
