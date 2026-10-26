using RouteBaseWeb.Models;
using RouteBaseWeb.Services.Interfaces;
using RouteBaseWeb.Services.Tracks;
using Microsoft.Extensions.Caching.Memory;

namespace RouteBaseWeb.Services;

/// <summary>
/// The coalesced half of GPS coordinate reads: decode → shape → cache-set, run once per identical
/// key via <see cref="SingleFlight"/> (a zoom storm collapses onto one execution).
///
/// Singleton, and that is the whole point of the class rather than an incidental lifetime. A
/// coalesced producer can outlive the request that started it — an aborting requester leaves the
/// shared work running for the waiters still attached — so a producer that closed over a scoped
/// <c>DbContext</c> would eventually read a disposed one, intermittently and only under
/// concurrency. <see cref="ActivityGpsService"/> is Scoped, so a lambda written there captures
/// <c>this</c> and puts its context one keystroke away from the producer. Hosting the producers
/// here keeps that context out of reach entirely: nothing scoped is injected, so nothing scoped
/// can be captured.
///
/// Visibility and ownership stay with the caller — they are request-scoped questions and must be
/// answered, along with the cache options, BEFORE handing work to this class.
/// </summary>
public sealed class GpsCoordinateCoalescer
{
    private readonly SingleFlight _singleFlight;
    private readonly ITrackReader _reader;
    private readonly GpsCoordinateCache _gpsCache;

    public GpsCoordinateCoalescer(SingleFlight singleFlight, ITrackReader reader, GpsCoordinateCache gpsCache)
    {
        _singleFlight = singleFlight;
        _reader       = reader;
        _gpsCache     = gpsCache;
    }

    /// <summary>One track, shaped and cached under <paramref name="cacheKey"/>. Non-positive
    /// <paramref name="maxPoints"/> means "every point" — callers that treat that as a different
    /// cache entry must route it themselves before calling.</summary>
    public Task<List<GpsCoordinate>> ShapeAsync(
        string cacheKey, TrackId trackId, int maxPoints, MemoryCacheEntryOptions opts, CancellationToken ct) =>
        _singleFlight.RunAsync(cacheKey, async innerCt =>
        {
            var track    = await _reader.GetTrackAsync(trackId, innerCt);
            var computed = maxPoints > 0
                ? TrackCoordinates.Downsample(track, maxPoints)
                : TrackCoordinates.NullToZero(track);
            _gpsCache.Set(cacheKey, computed, opts.SetSize(computed.Count));
            return computed;
        }, ct);

    /// <summary>Many tracks in one coalesced execution, cached as a single entry.</summary>
    public Task<Dictionary<int, List<GpsCoordinate>>> BulkAsync(
        string cacheKey, List<TrackId> sortedIds, int maxPointsPerTrack, MemoryCacheEntryOptions opts,
        CancellationToken ct) =>
        _singleFlight.RunAsync(cacheKey, async innerCt =>
        {
            var result = await BuildBulkCoordinatesAsync(sortedIds, maxPointsPerTrack, innerCt);
            _gpsCache.Set(cacheKey, result, opts.SetSize(result.Values.Sum(v => v.Count)));
            return result;
        }, ct);

    // Decode each visible track once (LRU-shared) and shape its coordinates. Tracks with no points —
    // or none surviving the non-NULL-coord filter — are absent from the map (matches the old GROUP BY,
    // which produced no group for an empty row set).
    private async Task<Dictionary<int, List<GpsCoordinate>>> BuildBulkCoordinatesAsync(
        List<TrackId> sortedIds, int maxPointsPerTrack, CancellationToken ct)
    {
        var result = new Dictionary<int, List<GpsCoordinate>>();
        foreach (var trackId in sortedIds)
        {
            var track = await _reader.GetTrackAsync(new TrackId(trackId.Value), ct);
            if (track == null) continue;

            var coords = maxPointsPerTrack > 0
                ? TrackCoordinates.Downsample(track, maxPointsPerTrack)
                : TrackCoordinates.Filtered(track);
            if (coords.Count > 0) result[trackId.Value] = coords;
        }
        return result;
    }
}
