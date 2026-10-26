using RouteBaseWeb.Models.Gpx;

namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// Format-neutral entry point for parsing an uploaded activity file (GPX / TCX / FIT).
/// Implementations sniff the content and delegate to the matching format parser, returning
/// the shared <see cref="GpxParseResult"/> contract (provider-agnostic summary + GPS points).
/// </summary>
public interface ITrackFileParser
{
    /// <summary>
    /// Parses the given activity-file stream. <paramref name="fileName"/> is used for the default
    /// activity name and, when <paramref name="useFilenameAsId"/> is true, as the ExternalId for
    /// cross-source duplicate detection. Format is decided by content, not the file extension.
    /// </summary>
    /// <returns>
    /// One result per activity in the file (≥1). Almost every file yields a single-element list; a
    /// chained FIT stream (multiple complete files concatenated) yields one result per file so each
    /// becomes its own activity (OBS-2). A parse/format failure is a single failed result.
    /// </returns>
    IReadOnlyList<GpxParseResult> Parse(Stream stream, string fileName, bool useFilenameAsId = false);
}
