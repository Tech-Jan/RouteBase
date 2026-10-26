using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Races;

namespace RouteBaseWeb.Tests.Services.Races;

/// <summary>
/// A course grid, without a track store behind it.
///
/// Everything that writes a chainage now measures it against the GRID — the one coordinate system the map,
/// the profile, the pacing anchors and the derived splits share — so the CRUD tests need a grid as much as
/// the replay tests always did. The real service decodes a blob and resamples it; these fixtures have no
/// blob, and what they assert has nothing to do with resampling.
///
/// Per-track lengths matter for more than multi-course fixtures: the grid and the course activity's
/// <c>TotalDistance</c> are DIFFERENT MEASUREMENTS, and a fixture that needs them to disagree (they do, by
/// ~0.5%, on any real import) sets only the one it is testing.
/// </summary>
internal sealed class FakeRaceCourseService : IRaceCourseService
{
    private readonly Dictionary<int, RaceCourseGrid?> _byTrack = new();
    private readonly RaceCourseGrid? _fallback;

    private FakeRaceCourseService(RaceCourseGrid? fallback) => _fallback = fallback;

    /// <summary>Every course is <paramref name="totalMeters"/> long, flat and straight.</summary>
    public static FakeRaceCourseService OfLength(double totalMeters, double stepMeters = 10.0) =>
        new(FlatGrid(totalMeters, stepMeters));

    /// <summary>No track has gridable geometry — what the real service returns for a GPS-less course.</summary>
    public static FakeRaceCourseService Unraceable() => new(null);

    /// <summary>Override one track's length. Fluent, so a fixture can chain it onto the constructor.</summary>
    public FakeRaceCourseService WithTrack(int trackId, double totalMeters, double stepMeters = 10.0)
    {
        _byTrack[trackId] = FlatGrid(totalMeters, stepMeters);
        return this;
    }

    /// <summary>Override one track to have no geometry at all, while the others still do.</summary>
    public FakeRaceCourseService WithUnraceableTrack(int trackId)
    {
        _byTrack[trackId] = null;
        return this;
    }

    /// <summary>
    /// A flat, straight grid. <c>TotalMeters</c> lands exactly on <paramref name="totalMeters"/> when it is a
    /// multiple of the step — the same <c>floor(total / step) * step</c> the real builder produces.
    /// </summary>
    public static RaceCourseGrid FlatGrid(double totalMeters, double stepMeters = 10.0)
    {
        var n = (int)Math.Floor(totalMeters / stepMeters) + 1;
        var distance = new double[n];
        var lat = new double[n];
        var lng = new double[n];
        var elevation = new double[n];
        for (var i = 0; i < n; i++)
        {
            distance[i] = i * stepMeters;
            lat[i] = 47.9;
            lng[i] = 8.1 + i * 0.0001;
            elevation[i] = 500;
        }
        return new RaceCourseGrid
        {
            Distance = distance, Lat = lat, Lng = lng,
            Elevation = elevation, GradePercent = new double[n], StepMeters = stepMeters,
        };
    }

    public Task<RaceCourseGrid?> GetCourseGridAsync(TrackId courseTrackId, CancellationToken ct = default) =>
        Task.FromResult(_byTrack.TryGetValue(courseTrackId.Value, out var grid) ? grid : _fallback);
}
