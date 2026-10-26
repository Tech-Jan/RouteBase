using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Tracks;
using Microsoft.Extensions.Caching.Memory;

namespace RouteBaseWeb.Services.Races;

/// <summary>Builds (and caches) the shared course grid for a race's course activity.</summary>
public interface IRaceCourseService
{
    /// <summary>
    /// The course grid for an activity's head track version. Null when the track has no GPS or is too
    /// short to grid. Callers pre-authorize the track id (same contract as ITrackReader).
    /// </summary>
    Task<RaceCourseGrid?> GetCourseGridAsync(TrackId courseTrackId, CancellationToken ct = default);
}

/// <inheritdoc />
public class RaceCourseService : IRaceCourseService
{
    private readonly ITrackReader _tracks;
    private readonly TrackContentStore _content;
    private readonly IMemoryCache _cache;
    private readonly ILogger<RaceCourseService> _logger;

    // The grid is a pure function of the track CONTENT, so the object sha is the key — not the track id.
    // Keyed by id alone it survived a version switch: refreshing (or reverting) a course's elevation moves
    // the head to a new object, and the replay went on serving the old grid until the process restarted.
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);

    public RaceCourseService(
        ITrackReader tracks, TrackContentStore content, IMemoryCache cache, ILogger<RaceCourseService> logger)
    {
        _tracks = tracks;
        _content = content;
        _cache = cache;
        _logger = logger;
    }

    public async Task<RaceCourseGrid?> GetCourseGridAsync(TrackId courseTrackId, CancellationToken ct = default)
    {
        var sha = await _content.GetHeadShaAsync(courseTrackId, ct);
        var key = $"race-course-grid:{courseTrackId}:{sha}";
        if (sha != null && _cache.TryGetValue(key, out RaceCourseGrid? cached)) return cached;

        var track = await _tracks.GetTrackAsync(courseTrackId, ct);
        if (track == null || track.Count < 2) return null;

        var lat = new List<double>(track.Count);
        var lng = new List<double>(track.Count);
        var alt = new List<double?>(track.Count);
        for (var i = 0; i < track.Count; i++)
        {
            // A point with no fix is not a place: TrackChannel reads its NULL coordinates as NaN, and one
            // NaN coordinate poisons the arc length of every point after it — and with it the whole grid.
            if (!double.IsFinite(track.Lat[i]) || !double.IsFinite(track.Lng[i])) continue;
            lat.Add(track.Lat[i]);
            lng.Add(track.Lng[i]);

            // A channel that EXISTS can still be full of holes: TrackChannel's per-point NULL reads as NaN,
            // which is what a DEM refresh leaves wherever it had no data (this course canoes and kayaks
            // across water). Taken for a value it poisons the whole smoothed profile.
            var sample = track.Altitude.IsNull ? double.NaN : track.Altitude[i];
            alt.Add(double.IsFinite(sample) ? sample : null);
        }
        if (lat.Count < 2) return null;

        RaceCourseGrid grid;
        try
        {
            grid = RaceCourseGridBuilder.Build(lat, lng, alt) with { SourceSha = sha };
        }
        catch (ArgumentException ex)
        {
            // A course too short to grid isn't an error worth throwing at a caller — it just isn't raceable.
            _logger.LogWarning(ex, "Track {TrackId} cannot be gridded as a race course", courseTrackId);
            return null;
        }

        if (sha != null) _cache.Set(key, grid, CacheTtl);
        return grid;
    }
}
