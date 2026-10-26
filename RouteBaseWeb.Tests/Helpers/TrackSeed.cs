using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Tracks;

namespace RouteBaseWeb.Tests.Helpers;

/// <summary>
/// Test seeding for the content/version world: encode a track's points via the production codec and store them
/// through the production <see cref="TrackContentStore"/> — object + version + head, exactly as an import does
/// (one write path, so a seed can never diverge from what the app actually writes). Decoding is bit-exact for
/// quantization-exact fixtures (6-decimal coords, 0.1 altitude/distance, 0.01 speed, whole-second timestamps,
/// integer hr/cadence/power).
///
/// Seeding the same track twice APPENDS a version and moves the head (it does not overwrite) — which is what
/// the "track changed under the session" fixtures rely on.
///
/// GPS ids are the ordinal ramp 0..n-1 (the codec does not store ids), so a test that once seeded contiguous
/// GPS_IDs 1..n and asserted a GPS_ID must re-derive the expectation to <c>id - 1</c>.
/// </summary>
public static class TrackSeed
{
    /// <summary>Encode <paramref name="points"/> (order = ordinal order) and store them as the track's content.</summary>
    public static Task SeedTrackAsync(RouteBaseDbContext db, TrackId trackId, IReadOnlyList<GpsData> points,
        CancellationToken ct = default)
    {
        var track = DecodedTrackExtensions.FromGpsDataList(points);
        return WriteContentAsync(db, trackId, track, ct);
    }

    /// <summary>Overload for the common lat/lng/time-only fixtures; fills a minimal GpsData per tuple.</summary>
    public static Task SeedTrackAsync(RouteBaseDbContext db, TrackId trackId,
        IEnumerable<(double lat, double lng, DateTime time)> points, CancellationToken ct = default)
    {
        var list = points.Select(p => new GpsData { GpsLat = p.lat, GpsLong = p.lng, DateTimeUtc = p.time }).ToList();
        return SeedTrackAsync(db, trackId, list, ct);
    }

    /// <summary>The content store over a test context — also the seam tests use to read versions/heads back.</summary>
    public static TrackContentStore ContentStore(RouteBaseDbContext db) =>
        new(db, new TrackObjectStore(db, TimeProvider.System), TimeProvider.System);

    private static async Task WriteContentAsync(RouteBaseDbContext db, TrackId trackId, DecodedTrack track, CancellationToken ct)
    {
        var encoded = TrackBlobCodec.Encode(track);
        await ContentStore(db).AppendVersionAsync(trackId, encoded, track.Count, ct: ct);
    }
}
