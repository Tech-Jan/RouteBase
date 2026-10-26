using RouteBaseWeb.Models.Gpx;

namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// Parses a Garmin FIT (binary) file into the shared provider-agnostic <see cref="GpxParseResult"/>
/// (summary + GPS points). See <see cref="ITrackFileParser"/> for routing. Returns one result per
/// chained file (OBS-2): a single file yields one, a truly-chained stream yields N.
/// </summary>
public interface IFitParsingService
{
    IReadOnlyList<GpxParseResult> Parse(Stream stream, string fileName, bool useFilenameAsId = false);
}
