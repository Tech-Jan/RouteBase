using RouteBaseWeb.Models;
namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// Exports a stored activity as a GPX 1.1 file.
/// </summary>
public interface IGpxExportService
{
    /// <summary>
    /// Builds a GPX 1.1 document for the given track and returns it as a UTF-8 MemoryStream.
    /// Returns null if the activity does not exist.
    /// </summary>
    Task<(Stream? Stream, string FileName)?> ExportAsync(TrackId trackId, CancellationToken ct = default);
}
