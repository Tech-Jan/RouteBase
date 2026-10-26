using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// Replaces GPS altitude values with Digital Elevation Model (DEM) data.
/// </summary>
public interface IElevationService
{
    /// <summary>
    /// Replaces altitude values in-place using DEM lookup and returns how many points were replaced.
    /// Points without lat/long are skipped. Falls back to original GPS altitude on API failure
    /// (a total failure returns 0 — callers may surface that to the user).
    /// </summary>
    Task<int> ApplyDemElevationAsync(List<GpsData> points);
}
