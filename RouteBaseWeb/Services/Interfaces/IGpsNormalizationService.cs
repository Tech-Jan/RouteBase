using RouteBaseWeb.Models;

namespace RouteBaseWeb.Services.Interfaces;

/// <summary>
/// Handles in-memory GPS signal normalization: altitude smoothing, speed smoothing,
/// and activity stat computation. No database access — operates purely on in-memory points.
/// </summary>
public interface IGpsNormalizationService
{
    /// <summary>
    /// Removes lateral GPS outliers in-place: any point implying an impossible speed from the previous
    /// kept point (a "teleport" from a bad wifi/cell fix) is dropped. Run before smoothing/stats so
    /// distance, pace, altitude, and the stored track are all computed on cleaned points.
    /// </summary>
    void RejectPositionOutliers(IList<GpsData> points);

    /// <summary>
    /// Applies spike rejection + curvature (arc-length) altitude smoothing to the points in-place.
    /// Points without altitude are skipped. The track is split at discontinuities (recording gaps, merge
    /// seams) and each continuous segment is smoothed independently — no state carries across a break,
    /// because the elevation step across one is real terrain, not a spike.
    /// </summary>
    void SmoothAltitudes(IList<GpsData> points);

    /// <summary>
    /// Trims the trailing dwell — the stretch at the end where the rider had arrived and only the GPS
    /// kept moving — off the end in-place, so total time, distance and end coordinates reflect the
    /// arrival rather than minutes parked at home or work. A point counts as dwelling when the rider
    /// gets no meaningful distance away from it over the following couple of minutes; judging net
    /// progress rather than speed is the whole point, because parked GPS jitter crosses any speed
    /// threshold continuously while going nowhere. Only a dwell that reaches the end of the track is
    /// trimmed, and only once it is long enough to be an arrival rather than a traffic light, so
    /// mid-track stops and slow-but-real finishes (a walked cool-down) are left alone. No-op if the
    /// track never moved. Needs coordinates and timestamps; independent of smoothed speeds.
    /// </summary>
    void TrimTrailingDwell(IList<GpsData> points);

    /// <summary>
    /// Computes distance, ascent/descent, total/moving time, and speed statistics
    /// from an ordered list of GPS points. Points must already have any desired smoothing applied.
    /// </summary>
    GpsNormalizationStats ComputeStats(IList<GpsData> points);
}
