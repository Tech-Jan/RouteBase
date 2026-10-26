using RouteBaseWeb.Models;
namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// Re-stat a track's elevation (B1-S2): general capability that works
/// on any owned track, imports included — segregated from <see cref="IRecordingFinalizationService"/> so
/// the Activities API depends on a neutral name, not a Recording-named interface. The implementation
/// (<see cref="RouteBaseWeb.Services.RecordingFinalizationService"/>) is unchanged.
/// </summary>
public interface IElevationRefreshService
{
    /// <summary>
    /// Re-applies DEM elevation to an existing track's GPS points, then re-runs
    /// altitude smoothing and recomputes ascent/descent stats. Use to update elevation
    /// for activities that were imported without DEM correction.
    /// Returns null when the track has no GPS points (nothing was refreshed);
    /// PointsReplaced == 0 with points present means the DEM source yielded nothing. EL-08.
    /// </summary>
    Task<ElevationRefreshResult?> RefreshElevationAsync(TrackId trackId);
}

/// <summary>Outcome of an elevation refresh — lets the API report an honest result. EL-08.</summary>
public sealed record ElevationRefreshResult(int PointsTotal, int PointsReplaced);
