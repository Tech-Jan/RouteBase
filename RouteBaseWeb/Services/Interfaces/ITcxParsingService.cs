using RouteBaseWeb.Models.Gpx;

namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// Parses a Garmin TCX (Training Center Database v2) file into the shared provider-agnostic
/// <see cref="GpxParseResult"/> (summary + GPS points). See <see cref="ITrackFileParser"/> for routing.
/// </summary>
public interface ITcxParsingService
{
    // One result per &lt;Activity&gt; in the file: a multi-activity TCX splits into N results, mirroring
    // the FIT parser (6b04eb43). A single-activity file yields a one-element list; unusable sessions are
    // dropped, so an empty list is possible. See <see cref="ITrackFileParser"/> for routing.
    IReadOnlyList<GpxParseResult> Parse(Stream stream, string fileName, bool useFilenameAsId = false);
}
