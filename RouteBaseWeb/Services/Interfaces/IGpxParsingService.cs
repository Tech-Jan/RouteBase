using RouteBaseWeb.Models.Gpx;

namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// Parses a GPX 1.1 file into a provider-agnostic activity summary and GPS points.
/// </summary>
public interface IGpxParsingService
{
    /// <summary>
    /// Parses the given GPX stream. <paramref name="fileName"/> is used for the default
    /// activity name and, when <paramref name="useFilenameAsId"/> is true, as the
    /// ExternalId (source "strava") for cross-source duplicate detection.
    /// </summary>
    // A GPX file is a single track import — always a single-element list. Returns the list contract
    // for uniformity with the other parsers (see <see cref="ITrackFileParser"/>).
    IReadOnlyList<GpxParseResult> Parse(Stream gpxStream, string fileName, bool useFilenameAsId = false);
}
